using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>One attachment of an opened message, as listed under the reader header.</summary>
    public sealed record AttachmentInfo(int Index, string FileName, string MimeType, string HumanSize);

    /// <summary>
    /// Lists and extracts attachments from the cached MIME of a message. Files land in the
    /// user's Downloads folder (name collisions get " (2)" style suffixes); "save all" puts
    /// every part into a subfolder named after the subject.
    /// </summary>
    public static class AttachmentService
    {
        /// <summary>The attachments of the cached message; empty when the body is not cached yet.</summary>
        public static List<AttachmentInfo> List(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            List<AttachmentInfo> result = [];
            var message = TryLoadMessage(account, folderFullName, summary);
            if (message == null) return result;

            int index = 0;
            foreach (var attachment in message.Attachments)
            {
                long size = attachment is MimePart sized ? EstimateSize(sized) : 0;
                result.Add(new AttachmentInfo(index, DisplayName(attachment, index), attachment.ContentType.MimeType, HumanSize(size)));
                index++;
            }
            return result;
        }

        /// <summary>Filename shown for the part: its own FileName, an attached message's subject + ".eml", or a numbered fallback.</summary>
        static string DisplayName(MimeEntity attachment, int index) =>
            attachment is MimePart part
                ? part.FileName ?? $"attachment-{index + 1}"
                : ((MessagePart)attachment).Message?.Subject is { Length: > 0 } subject ? subject + ".eml" : $"message-{index + 1}.eml";

        /// <summary>Decodes one attachment into the Downloads folder and returns the saved path.</summary>
        public static async Task<string> SaveAsync(MailAccountData account, string folderFullName, MailMessageSummary summary, int attachmentIndex)
        {
            var message = TryLoadMessage(account, folderFullName, summary)
                ?? throw new InvalidOperationException("The message is not cached yet.");
            var attachment = message.Attachments.ElementAtOrDefault(attachmentIndex)
                ?? throw new InvalidOperationException("Attachment not found in the message.");

            string target = UniquePath(DownloadsFolder(), DisplayName(attachment, attachmentIndex));
            await WriteEntityAsync(attachment, target);
            return target;
        }

        /// <summary>Saves every attachment into Downloads\&lt;subject&gt;\ and returns that folder.</summary>
        public static async Task<string> SaveAllAsync(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            var message = TryLoadMessage(account, folderFullName, summary)
                ?? throw new InvalidOperationException("The message is not cached yet.");

            string targetFolder = Path.Combine(DownloadsFolder(), SafeFileStem(summary.Subject, "attachments"));
            Directory.CreateDirectory(targetFolder);

            int index = 0;
            foreach (var attachment in message.Attachments)
            {
                await WriteEntityAsync(attachment, UniquePath(targetFolder, DisplayName(attachment, index)));
                index++;
            }
            return targetFolder;
        }

        static MimeMessage? TryLoadMessage(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            try
            {
                return MessageStore.TryLoadMimeMessage(account.Id, folderFullName, summary.Uid);
            }
            catch (Exception ex)
            {
                Log($"Attachment listing failed: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        /// <summary>Writes one attachment to disk. A part with no content (or an attached message
        /// that failed to parse) leaves an empty file rather than throwing mid-save.</summary>
        static async Task WriteEntityAsync(MimeEntity attachment, string path)
        {
            await using var output = File.Create(path);
            if (attachment is MimePart part)
            {
                if (part.Content != null)
                    await part.Content.DecodeToAsync(output);
            }
            else if (attachment is MessagePart { Message: { } inner })
            {
                await inner.WriteToAsync(output);
            }
        }

        /// <summary>Shared by attachment saves and message exports (MessageExportService).</summary>
        internal static string DownloadsFolder()
        {
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(downloads) ? downloads : AppPaths.UserData;
        }

        /// <summary>Filesystem-safe stem from <paramref name="text"/> (invalid chars become "_", capped at 60 chars); <paramref name="fallback"/> replaces an empty text. Shared with MessageExportService.</summary>
        internal static string SafeFileStem(string text, string fallback)
        {
            const int MaxStemLength = 60;
            string stem = string.Join("_", (text.Length > 0 ? text : fallback)
                .Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
            return stem.Length > MaxStemLength ? stem[..MaxStemLength] : stem;
        }

        /// <summary>Sanitizes the name and dodges collisions with " (2)"-style suffixes.</summary>
        internal static string UniquePath(string folder, string fileName)
        {
            string sanitized = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            string path = Path.Combine(folder, sanitized);
            string stem = Path.GetFileNameWithoutExtension(sanitized);
            string extension = Path.GetExtension(sanitized);
            for (int copy = 2; File.Exists(path); copy++)
                path = Path.Combine(folder, $"{stem} ({copy}){extension}");
            return path;
        }

        static long EstimateSize(MimePart part)
        {
            try
            {
                if (part.Content == null) return 0;
                using var counter = new MemoryStream();
                part.Content.DecodeTo(counter);
                return counter.Length;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Emoji chip icon by content type / extension.</summary>
        public static string IconFor(AttachmentInfo info)
        {
            string ext = Path.GetExtension(info.FileName).ToLowerInvariant();
            if (info.MimeType.StartsWith("image/")) return "🖼️";
            if (info.MimeType.StartsWith("audio/")) return "🎵";
            if (info.MimeType.StartsWith("video/")) return "🎬";
            return ext switch
            {
                ".pdf" => "📕",
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "🗜️",
                ".doc" or ".docx" or ".odt" => "📄",
                ".xls" or ".xlsx" or ".csv" => "📊",
                ".ppt" or ".pptx" => "📽️",
                ".eml" => "✉️",
                _ => "📎"
            };
        }

        static string HumanSize(long bytes) => bytes switch
        {
            <= 0 => "",
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes / (1024.0 * 1024.0):0.#} MB"
        };
    }
}
