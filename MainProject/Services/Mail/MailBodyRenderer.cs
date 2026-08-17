using System.Net;
using System.Text.RegularExpressions;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>Rendered document plus whether remote images were stripped (drives the allow-once banner).</summary>
    public sealed record RenderedBody(string Html, bool RemoteImagesBlocked);

    /// <summary>
    /// Turns a cached .eml into a sanitized HTML document for the reader's sandboxed iframe.
    /// Sanitizing strips active content (scripts, objects, event handlers, javascript: URLs),
    /// inlines cid: images from the message's own parts, and blocks remote images per
    /// <see cref="Settings.ExternalImages"/>. Plain-text bodies are HTML-encoded into a &lt;pre&gt;.
    /// </summary>
    public static partial class MailBodyRenderer
    {
        /// <summary>1x1 transparent GIF replacing blocked remote images (keeps layout, loads nothing).</summary>
        const string BlockedImagePlaceholder = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

        [GeneratedRegex(@"<script[\s\S]*?</script\s*>|<script[^>]*/>", RegexOptions.IgnoreCase)]
        private static partial Regex ScriptBlocks();

        [GeneratedRegex(@"<(object|embed|base|iframe|form|applet|meta)\b[^>]*>|</(object|embed|base|iframe|form|applet|meta)\s*>", RegexOptions.IgnoreCase)]
        private static partial Regex ForbiddenTags();

        [GeneratedRegex(@"\son\w+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
        private static partial Regex EventAttributes();

        [GeneratedRegex(@"(href|src|action)\s*=\s*([""']?)\s*javascript:[^""'>\s]*\2", RegexOptions.IgnoreCase)]
        private static partial Regex JavascriptUrls();

        [GeneratedRegex(@"(<img\b[^>]*?\ssrc\s*=\s*)([""']?)(https?://[^""'>\s]+)\2", RegexOptions.IgnoreCase)]
        private static partial Regex RemoteImageSources();

        /// <summary>
        /// The sanitized HTML document for the message, or null when the full body is not cached
        /// yet (caller downloads it first). Never throws — a broken MIME falls back to the preview.
        /// </summary>
        public static RenderedBody? Render(MailAccountData account, string folderFullName, MailMessageSummary summary, bool allowRemoteImages = false)
        {
            byte[]? mimeBytes = MessageStore.TryLoadFullMessage(account.Id, folderFullName, summary.Uid);
            if (mimeBytes == null) return null;

            try
            {
                using var stream = new MemoryStream(mimeBytes);
                var message = MimeMessage.Load(stream);

                bool blocked = false;
                string body = !string.IsNullOrWhiteSpace(message.HtmlBody)
                    ? Sanitize(message.HtmlBody, message, allowRemoteImages, ref blocked)
                    : $"<pre>{WebUtility.HtmlEncode(message.TextBody ?? summary.PreviewText)}</pre>";

                return new RenderedBody(WrapDocument(body), blocked);
            }
            catch (Exception ex)
            {
                Log($"Body render failed for uid {summary.Uid}: {ex.Message}", LogLevel.Warning);
                return new RenderedBody(WrapDocument($"<pre>{WebUtility.HtmlEncode(summary.PreviewText)}</pre>"), false);
            }
        }

        static string Sanitize(string html, MimeMessage message, bool allowRemoteImages, ref bool blockedRemoteImages)
        {
            html = ScriptBlocks().Replace(html, string.Empty);
            html = ForbiddenTags().Replace(html, string.Empty);
            html = EventAttributes().Replace(html, string.Empty);
            html = JavascriptUrls().Replace(html, "$1=$2about:blank$2");
            html = InlineCidImages(html, message);

            if (!allowRemoteImages && Settings.ExternalImages.Value == ExternalImagesPolicy.Block && RemoteImageSources().IsMatch(html))
            {
                blockedRemoteImages = true;
                html = RemoteImageSources().Replace(html, $"$1$2{BlockedImagePlaceholder}$2");
            }

            return html;
        }

        /// <summary>Replaces cid: image references with data: URIs built from the message's own inline parts.</summary>
        static string InlineCidImages(string html, MimeMessage message)
        {
            foreach (var part in message.BodyParts.OfType<MimePart>())
            {
                if (string.IsNullOrEmpty(part.ContentId)) continue;

                using var buffer = new MemoryStream();
                part.Content?.DecodeTo(buffer);
                string dataUri = $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(buffer.ToArray())}";
                html = html
                    .Replace($"cid:{part.ContentId}", dataUri, StringComparison.OrdinalIgnoreCase)
                    .Replace($"cid:<{part.ContentId}>", dataUri, StringComparison.OrdinalIgnoreCase);
            }
            return html;
        }

        /// <summary>Neutral readable defaults so plain messages look tidy inside the frame in any app theme.</summary>
        static string WrapDocument(string body) =>
            "<!DOCTYPE html><html><head><style>" +
            "body{font-family:'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:#33333a;background:#ffffff;" +
            "margin:12px;line-height:1.55;font-size:14px;word-break:break-word;}" +
            "img{max-width:100%;height:auto;}pre{white-space:pre-wrap;font-family:inherit;}" +
            "a{color:#d14d8b;}blockquote{border-left:3px solid #f0c0d4;margin-left:0;padding-left:12px;color:#7d5a6e;}" +
            "</style></head><body>" + body + "</body></html>";
    }
}
