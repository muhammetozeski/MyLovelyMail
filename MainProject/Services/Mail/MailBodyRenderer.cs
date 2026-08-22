using System.Net;
using System.Text.RegularExpressions;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.Constants.ThemeConstants;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>Rendered document, whether remote images were stripped (drives the allow-once banner) and whether a quoted-history fold was inserted.</summary>
    public sealed record RenderedBody(string Html, bool RemoteImagesBlocked, bool QuotedTextFolded = false);

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
        public static RenderedBody? Render(MailAccountData account, string folderFullName, MailMessageSummary summary,
            bool allowRemoteImages = false, bool foldQuotedText = true)
        {
            try
            {
                if (MessageStore.TryLoadMimeMessage(account.Id, folderFullName, summary.Uid) is not { } message)
                    return null;

                bool blocked = false;
                bool folded = false;
                bool fold = foldQuotedText && Settings.FoldQuotedText.Value;
                string body;
                if (!string.IsNullOrWhiteSpace(message.HtmlBody))
                {
                    body = Sanitize(message.HtmlBody, message, allowRemoteImages, ref blocked);
                    if (fold) body = FoldHtmlQuotes(body, ref folded);
                }
                else
                {
                    body = BuildPlainBody(message.TextBody ?? summary.PreviewText, fold, ref folded);
                }

                return new RenderedBody(WrapDocument(body), blocked, folded);
            }
            catch (Exception ex)
            {
                Log($"Body render failed for uid {summary.Uid}: {ex.Message}", LogLevel.Warning);

                // A body that will not parse is a corpse of an interrupted write, and returning a
                // preview stub hid it forever: the row opened instantly and showed 160 characters
                // of a real message, while HasFullMessage kept reporting the file as cached.
                // Dropping it puts the caller on the download path it already has for a missing
                // body. Local folders are exempt — there is no server copy to fetch it back from.
                if (!folderFullName.StartsWith(MessageStore.LocalFolderPrefix))
                {
                    MessageStore.DeleteCachedBody(account.Id, folderFullName, summary.Uid);
                    Log($"Dropped the unreadable cached body of uid {summary.Uid}; it will be downloaded again.");
                    return null;
                }
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

        #region Quoted-history folding

        /// <summary>Below this many characters of new text there is nothing worth folding away — a message that IS a quote must not collapse to an empty body.</summary>
        const int MinimumHeadLength = 40;

        const string FoldOpen = "<details class=\"mlm-quote\"><summary>Show quoted text</summary>";
        const string FoldClose = "</details>";

        /// <summary>Markers every major client puts at the head of the quoted history it appends.</summary>
        static readonly string[] HtmlQuoteMarkers =
            ["<blockquote", "gmail_quote", "moz-cite-prefix", "divRplyFwdMsg", "stopSpelling"];

        /// <summary>Where quoted history starts in a plain-text body. Shared with the attachment-intent scan so both agree on what "quoted" means.</summary>
        [GeneratedRegex(@"^\s*(>|-{2,}\s*Original Message\s*-{2,}|On .{0,160}\bwrote:\s*)$", RegexOptions.IgnoreCase)]
        internal static partial Regex PlainQuoteStart();

        /// <summary>
        /// Wraps everything from the first quote marker onward in a JS-free &lt;details&gt;. No tag
        /// balancing is attempted (regex cannot balance HTML, same accepted constraint as Sanitize):
        /// the closing tag lands at the very end, which the browser reconciles.
        /// </summary>
        static string FoldHtmlQuotes(string html, ref bool folded)
        {
            int cut = HtmlQuoteMarkers
                .Select(marker => html.IndexOf(marker, StringComparison.OrdinalIgnoreCase))
                .Where(index => index >= MinimumHeadLength)
                .DefaultIfEmpty(-1)
                .Min();
            if (cut < 0) return html;

            folded = true;
            return html[..cut] + FoldOpen + html[cut..] + FoldClose;
        }

        /// <summary>Plain-text bodies: the head stays visible, the attribution line and everything under it move into the fold.</summary>
        static string BuildPlainBody(string text, bool fold, ref bool folded)
        {
            if (fold)
            {
                var lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!PlainQuoteStart().IsMatch(lines[i].TrimEnd('\r'))) continue;

                    string head = string.Join('\n', lines[..i]);
                    if (head.Trim().Length < MinimumHeadLength) break;

                    folded = true;
                    return $"<pre>{WebUtility.HtmlEncode(head)}</pre>" +
                           FoldOpen + $"<pre>{WebUtility.HtmlEncode(string.Join('\n', lines[i..]))}</pre>" + FoldClose;
                }
            }
            return $"<pre>{WebUtility.HtmlEncode(text)}</pre>";
        }

        #endregion

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

        /// <summary>Wraps the sanitized body in a minimal document whose colors come from <see cref="AppColors.MailCanvas"/>, the same source the iframe element uses.</summary>
        /// <summary>Design text size inside the reader, before <see cref="Settings.ReaderTextScalePercent"/>.</summary>
        const int BaseFontPx = 14;

        const int MinScalePercent = 70, MaxScalePercent = 200;

        /// <summary>
        /// The one piece of type the user could not change: UiScalePercent scales the whole shell
        /// and the density setting only touches list rows, so making mail readable meant resizing
        /// the entire app. Read inline like the other settings this method already consults, so
        /// every caller — the reader and the debug API alike — gets the scaled document.
        /// </summary>
        static string WrapDocument(string body)
        {
            int scale = Math.Clamp(Settings.ReaderTextScalePercent.Value, MinScalePercent, MaxScalePercent);
            int fontPx = BaseFontPx * scale / 100;
            return "<!DOCTYPE html><html><head><style>" +
                $"body{{font-family:'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:{AppColors.MailCanvas.Text};background:{AppColors.MailCanvas.Background};" +
                $"margin:12px;line-height:1.55;font-size:{fontPx}px;word-break:break-word;}}" +
                // plaintext direction: an Arabic or Hebrew paragraph inside an otherwise
                // left-to-right message decides its own direction from its first strong character,
                // rather than inheriting the document's and reading backwards.
                "img{max-width:100%;height:auto;}pre{white-space:pre-wrap;font-family:inherit;unicode-bidi:plaintext;}" +
                "blockquote{unicode-bidi:plaintext;}" +
                $"a{{color:{AppColors.MailCanvas.Link};}}blockquote{{border-left:3px solid {AppColors.MailCanvas.QuoteBorder};margin-left:0;padding-left:12px;color:{AppColors.MailCanvas.QuoteText};}}" +
                $"details.mlm-quote>summary{{cursor:pointer;list-style:none;display:inline-block;margin:8px 0;padding:2px 10px;border-radius:9999px;" +
                // em, not px: the pill has to grow with the text it sits beside.
                $"background:{AppColors.MailCanvas.QuoteBorder};color:{AppColors.MailCanvas.QuoteText};font-size:0.85em;}}" +
                "details.mlm-quote>summary::-webkit-details-marker{display:none;}" +
                // dir=auto: the document takes its direction from its own first strong character
                // instead of being forced left-to-right.
                "</style></head><body dir=\"auto\">" + body + "</body></html>";
        }
    }
}
