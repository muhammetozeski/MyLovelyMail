using System.Text;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>Where a folder was written, how many messages made it, and how many had no cached body.</summary>
    public readonly record struct MailboxExportResult(string Path, int Written, int SkippedUncached);

    /// <summary>
    /// Writes a whole folder as one mbox file. Nothing else in the app moves more than a single
    /// message, and the migration bundle is this app's own UserData zip rather than something
    /// another client can read — mbox is what Thunderbird, Apple Mail and every archiver import,
    /// so this is the difference between the mail living here and the mail being the user's.
    /// <para>
    /// Everything written is already on disk under <see cref="MessageStore.MessagePath"/>; no
    /// protocol traffic, works offline.
    /// </para>
    /// </summary>
    public static class MailboxExportService
    {
        const string Extension = ".mbox";

        /// <summary>The pseudo-sender every mbox separator carries; readers key on the line, not on this name.</summary>
        const string SeparatorSender = "MAILER-DAEMON";

        public static MailboxExportResult ExportFolder(MailAccountData account, string folderFullName)
        {
            string path = AttachmentService.UniquePath(
                AttachmentService.DownloadsFolder(),
                AttachmentService.SafeFileStem(folderFullName, "mailbox") + Extension);

            int written = 0, skipped = 0;
            using (var file = File.Create(path))
            {
                // Oldest first: mbox is an append-ordered format and readers show it in file order.
                foreach (var summary in MessageStore.GetSummaries(account.Id, folderFullName).OrderBy(static s => s.DateUtc))
                {
                    byte[]? mime = MessageStore.TryLoadFullMessage(account.Id, folderFullName, summary.Uid);
                    if (mime == null)
                    {
                        // Counted, never silently dropped — a partial export that claims to be
                        // complete is worse than one that says what it left out.
                        skipped++;
                        continue;
                    }

                    WriteSeparator(file, summary.DateUtc);
                    WriteEscapedBody(file, mime);
                    file.WriteByte((byte)'\n');
                    written++;
                }
            }

            Log($"Exported '{folderFullName}' as mbox: {written} message(s) written, {skipped} without a cached body.");
            return new MailboxExportResult(path, written, skipped);
        }

        static void WriteSeparator(Stream file, DateTime dateUtc)
        {
            // asctime, the shape every mbox reader expects after "From <sender> ".
            string stamp = dateUtc.ToString("ddd MMM d HH:mm:ss yyyy", Constants.UiCulture.Display);
            byte[] separator = Encoding.ASCII.GetBytes($"From {SeparatorSender} {stamp}\n");
            file.Write(separator);
        }

        /// <summary>
        /// mboxrd escaping: a body line that itself starts with "From " (or an already-escaped
        /// ">From ") gets one more ">", so no line inside a message can be mistaken for the
        /// separator that starts the next one. Without it, a message quoting "From " splits into
        /// two on import.
        /// </summary>
        static void WriteEscapedBody(Stream file, byte[] mime)
        {
            int lineStart = 0;
            for (int i = 0; i <= mime.Length; i++)
            {
                bool endOfMessage = i == mime.Length;
                if (!endOfMessage && mime[i] != (byte)'\n') continue;

                int lineLength = i - lineStart;
                if (lineLength > 0 || !endOfMessage)
                {
                    if (StartsWithFrom(mime, lineStart, lineLength)) file.WriteByte((byte)'>');
                    file.Write(mime, lineStart, lineLength);
                    if (!endOfMessage) file.WriteByte((byte)'\n');
                }
                lineStart = i + 1;
            }
            // Every message must end on its own line or the next separator lands mid-line.
            if (mime.Length > 0 && mime[^1] != (byte)'\n') file.WriteByte((byte)'\n');
        }

        static bool StartsWithFrom(byte[] mime, int start, int length)
        {
            int offset = start;
            while (offset < start + length && mime[offset] == (byte)'>') offset++;

            ReadOnlySpan<byte> from = "From "u8;
            if (start + length - offset < from.Length) return false;
            return mime.AsSpan(offset, from.Length).SequenceEqual(from);
        }
    }
}
