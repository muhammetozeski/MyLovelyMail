using System.Net;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Saves an opened message to the user's downloads (<see cref="UserDownloads"/>): .eml is the cached raw MIME byte-for-byte,
    /// .html is the sanitized rendered body (remote images allowed — the snapshot should be whole)
    /// with a From/To/Date/Subject header block on top. The .html doubles as the print path:
    /// open it in a browser and Ctrl+P. Both require the body to be cached, which opening the
    /// message in the reader already guarantees.
    /// </summary>
    public static class MessageExportService
    {
        /// <summary>Writes the raw MIME and returns the saved path.</summary>
        public static string ExportEml(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            byte[] mimeBytes = MessageStore.TryLoadFullMessage(account.Id, folderFullName, summary.Uid)
                ?? throw new InvalidOperationException("The message body is not cached yet.");
            string written = AttachmentService.UniquePath(UserDownloads.WriteFolder,
                AttachmentService.SafeFileStem(summary.Subject, $"message-{summary.Uid}") + ".eml");
            File.WriteAllBytes(written, mimeBytes);
            string path = UserDownloads.Publish(written);
            Log($"Exported uid {summary.Uid} as eml: {path}");
            return path;
        }

        /// <summary>Writes the rendered body as a standalone HTML document and returns the saved path.</summary>
        public static string ExportHtml(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            // Saved/printed copies stay whole: no folded-away history, same reason images are allowed.
            var rendered = MailBodyRenderer.Render(account, folderFullName, summary, allowRemoteImages: true, foldQuotedText: false)
                ?? throw new InvalidOperationException("The message body is not cached yet.");

            string headerBlock =
                $"<div style=\"font-family:sans-serif;border-bottom:1px solid #ccc;padding:8px 0;margin-bottom:12px\">" +
                $"<div><b>From:</b> {WebUtility.HtmlEncode($"{summary.FromName} <{summary.FromAddress}>")}</div>" +
                $"<div><b>To:</b> {WebUtility.HtmlEncode(summary.ToAddresses)}</div>" +
                $"<div><b>Date:</b> {(account.TorOnly ? summary.DateUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture) : summary.DateUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))}</div>" +
                $"<div><b>Subject:</b> {WebUtility.HtmlEncode(summary.Subject)}</div></div>";

            string written = AttachmentService.UniquePath(UserDownloads.WriteFolder,
                AttachmentService.SafeFileStem(summary.Subject, $"message-{summary.Uid}") + ".html");
            // Keep the policy in the document head; a preceding div would make browsers parse
            // the generated head as body content and ignore its Content-Security-Policy meta.
            int bodyStart = rendered.Html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            int contentStart = bodyStart < 0 ? -1 : rendered.Html.IndexOf('>', bodyStart) + 1;
            if (contentStart <= 0) throw new InvalidOperationException("The rendered message has no document body.");
            File.WriteAllText(written, rendered.Html.Insert(contentStart, headerBlock));
            string path = UserDownloads.Publish(written);
            Log($"Exported uid {summary.Uid} as html: {path}");
            return path;
        }
    }
}
