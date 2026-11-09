using System.Net;
using System.Text;
using MyLovelyMail.MainProject.Constants;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Exports one opened message to the user's chosen path or Downloads folder, either as raw RFC 5322
    /// (<c>.eml</c>) or as a self-contained <c>.html</c> file, and optionally reveals the saved file in Explorer.
    /// </summary>
    public static class MessageExportService
    {
        /// <summary>Builds the default safe filename for exporting a message.</summary>
        public static string DefaultFileName(MailMessageSummary summary, string extension)
        {
            string date = summary.DateUtc.ToLocalTime().ToString("yyyy-MM-dd", UiCulture.Display);
            string stem = SafeFileName($"{date} - {summary.Subject}");
            return stem + (extension.StartsWith('.') ? extension : "." + extension);
        }

        /// <summary>Exports raw RFC 5322 (.eml) to Downloads (throwing if uncached, used by reader &amp; DebugApi).</summary>
        public static string ExportEml(MailAccountData account, string folderFullName, MailMessageSummary summary) =>
            SaveAsEml(account, folderFullName, summary, revealInExplorer: true)
            ?? throw new InvalidOperationException("The message body is not cached yet — open it once while online.");

        /// <summary>Exports rendered message (.html) to Downloads (throwing if uncached, used by reader &amp; DebugApi).</summary>
        public static string ExportHtml(MailAccountData account, string folderFullName, MailMessageSummary summary) =>
            SaveAsHtml(account, folderFullName, summary, revealInExplorer: true)
            ?? throw new InvalidOperationException("The message body is not cached yet — open it once while online.");

        /// <summary>Writes the cached raw MIME bytes to Downloads as <c>.eml</c>; returns the written path, or null when the body is not cached yet.</summary>
        public static string? SaveAsEml(MailAccountData account, string folderFullName, MailMessageSummary summary, bool revealInExplorer = true)
        {
            byte[]? raw = MessageStore.TryLoadFullMessage(account.Id, folderFullName, summary.Uid);
            if (raw == null) return null;

            string path = NextPath(summary, ".eml");
            File.WriteAllBytes(path, raw);
            string published = UserDownloads.Publish(path);
            if (revealInExplorer) FileSaveHelper.RevealInFileExplorer(published);
            return published;
        }

        /// <summary>Writes the cached raw MIME bytes to an explicit destination path as <c>.eml</c>.</summary>
        public static string? SaveAsEmlToPath(MailAccountData account, string folderFullName, MailMessageSummary summary, string destinationPath, bool revealInExplorer = true)
        {
            byte[]? raw = MessageStore.TryLoadFullMessage(account.Id, folderFullName, summary.Uid);
            if (raw == null) return null;

            string fullPath = Path.GetFullPath(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, raw);
            string published = UserDownloads.Publish(fullPath);
            if (revealInExplorer) FileSaveHelper.RevealInFileExplorer(published);
            return published;
        }

        /// <summary>Writes the rendered message (header block + sanitized body) to Downloads as <c>.html</c>; returns the written path, or null when the body is not cached yet.</summary>
        public static string? SaveAsHtml(MailAccountData account, string folderFullName, MailMessageSummary summary, bool revealInExplorer = true)
        {
            string? html = BuildStandaloneHtml(account, folderFullName, summary);
            if (html == null) return null;

            string path = NextPath(summary, ".html");
            File.WriteAllText(path, html, Encoding.UTF8);
            string published = UserDownloads.Publish(path);
            if (revealInExplorer) FileSaveHelper.RevealInFileExplorer(published);
            return published;
        }

        /// <summary>Writes the rendered message to an explicit destination path as <c>.html</c>.</summary>
        public static string? SaveAsHtmlToPath(MailAccountData account, string folderFullName, MailMessageSummary summary, string destinationPath, bool revealInExplorer = true)
        {
            string? html = BuildStandaloneHtml(account, folderFullName, summary);
            if (html == null) return null;

            string fullPath = Path.GetFullPath(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, html, Encoding.UTF8);
            string published = UserDownloads.Publish(fullPath);
            if (revealInExplorer) FileSaveHelper.RevealInFileExplorer(published);
            return published;
        }

        static string? BuildStandaloneHtml(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            var rendered = MailBodyRenderer.Render(account, folderFullName, summary, allowRemoteImages: false);
            if (rendered == null) return null;
            string enc(string s) => WebUtility.HtmlEncode(s);
            return $$"""
                <!doctype html>
                <html><head><meta charset="utf-8"><title>{{enc(summary.Subject)}}</title>
                <style>
                    body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; max-width: 780px; margin: 24px auto; padding: 0 16px; color: #1F1F23; }
                    .hdr { border-bottom: 1px solid #E4E4E8; padding-bottom: 12px; margin-bottom: 16px; font-size: 13px; color: #555; }
                    .hdr h1 { font-size: 20px; color: #1F1F23; margin: 0 0 8px; }
                </style></head><body>
                <div class="hdr">
                    <h1>{{enc(summary.Subject)}}</h1>
                    <div><b>From:</b> {{enc(summary.FromName)}} &lt;{{enc(summary.FromAddress)}}&gt;</div>
                    <div><b>To:</b> {{enc(summary.ToAddresses)}}</div>
                    <div><b>Date:</b> {{summary.DateUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm", UiCulture.Display)}}</div>
                </div>
                {{rendered.Html}}
                </body></html>
                """;
        }

        static string NextPath(MailMessageSummary summary, string extension)
        {
            string folder = UserDownloads.WriteFolder;
            Directory.CreateDirectory(folder);

            string date = summary.DateUtc.ToLocalTime().ToString("yyyy-MM-dd", UiCulture.Display);
            string stem = SafeFileName($"{date} - {summary.Subject}");
            string path = Path.Combine(folder, stem + extension);
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(folder, $"{stem} ({n}){extension}");
            return path;
        }

        static string SafeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(invalid.Contains(c) ? '_' : c);
            string cleaned = sb.ToString().Trim().TrimEnd('.');
            if (cleaned.Length > 80) cleaned = cleaned[..80].TrimEnd();
            return cleaned.Length == 0 ? "message" : cleaned;
        }
    }
}
