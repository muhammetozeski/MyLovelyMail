using System.Net;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Saves an opened message to the Downloads folder: .eml is the cached raw MIME byte-for-byte,
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
            string path = AttachmentService.UniquePath(AttachmentService.DownloadsFolder(),
                AttachmentService.SafeFileStem(summary.Subject, $"message-{summary.Uid}") + ".eml");
            File.WriteAllBytes(path, mimeBytes);
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
                $"<div><b>Date:</b> {summary.DateUtc.ToLocalTime():yyyy-MM-dd HH:mm}</div>" +
                $"<div><b>Subject:</b> {WebUtility.HtmlEncode(summary.Subject)}</div></div>";

            string path = AttachmentService.UniquePath(AttachmentService.DownloadsFolder(),
                AttachmentService.SafeFileStem(summary.Subject, $"message-{summary.Uid}") + ".html");
            File.WriteAllText(path, headerBlock + rendered.Html);
            Log($"Exported uid {summary.Uid} as html: {path}");
            return path;
        }
    }
}
