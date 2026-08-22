using System.Net;
using System.Text.RegularExpressions;
using MyLovelyMail.MainProject.Constants.ThemeConstants;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// The signature is an HTML document. One stored string serves three jobs: the HTML that goes
    /// out in the message, the plain text that goes out beside it (and that the compose box shows,
    /// because that box is a textarea), and the preview the settings page renders.
    /// <para>
    /// A value with no angle bracket in it is treated as plain text and escaped. That keeps every
    /// signature written before this existed working, with no migration step and nothing to undo.
    /// </para>
    /// </summary>
    public static partial class SignatureService
    {
        /// <summary>RFC 3676 signature delimiter; mail clients fold everything under it.</summary>
        public const string Delimiter = "\n\n-- \n";

        [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
        private static partial Regex LineBreaks();

        [GeneratedRegex(@"</(p|div|tr|li|h[1-6]|table|blockquote)\s*>", RegexOptions.IgnoreCase)]
        private static partial Regex BlockEnds();

        [GeneratedRegex(@"<(script|style)[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase)]
        private static partial Regex ScriptAndStyleBlocks();

        [GeneratedRegex(@"<[^>]+>")]
        private static partial Regex AnyTag();

        [GeneratedRegex(@"\n{3,}")]
        private static partial Regex ExtraBlankLines();

        [GeneratedRegex(@"<script[\s\S]*?</script\s*>|<script[^>]*/>", RegexOptions.IgnoreCase)]
        private static partial Regex ScriptBlocks();

        [GeneratedRegex(@"\son\w+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
        private static partial Regex EventAttributes();

        [GeneratedRegex(@"(href|src|action)\s*=\s*([""']?)\s*javascript:[^""'>\s]*\2", RegexOptions.IgnoreCase)]
        private static partial Regex JavascriptUrls();

        /// <summary>True when the stored value is HTML rather than a line of plain text.</summary>
        public static bool IsHtml(string signature) => signature.Contains('<');

        /// <summary>The signature as HTML, ready to drop into a message's html part.</summary>
        public static string ToHtml(string signature)
        {
            if (signature.Length == 0) return string.Empty;
            return IsHtml(signature) ? signature : TextToHtml(signature);
        }

        /// <summary>
        /// The signature as plain text: what the text part of the message carries and what the
        /// compose box shows. Scripts and stylesheets are dropped whole — their source is not
        /// something a reader should see as words.
        /// </summary>
        public static string ToPlainText(string signature)
        {
            if (signature.Length == 0) return string.Empty;
            if (!IsHtml(signature)) return signature;

            string text = ScriptAndStyleBlocks().Replace(signature, string.Empty);
            text = LineBreaks().Replace(text, "\n");
            text = BlockEnds().Replace(text, "\n");
            text = AnyTag().Replace(text, string.Empty);
            text = WebUtility.HtmlDecode(text);
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            return ExtraBlankLines().Replace(text, "\n\n").Trim();
        }

        /// <summary>
        /// The signature as it leaves in a message. The preview runs whatever the user wrote,
        /// including script — that is their own page in their own app. What goes out is a
        /// different question: no mail program runs script in a delivered message, so a script tag
        /// can only ever cost the message a spam score. The markup and the CSS go as written.
        /// </summary>
        public static string ToOutgoingHtml(string signature)
        {
            string html = ToHtml(signature);
            if (html.Length == 0) return html;
            html = ScriptBlocks().Replace(html, string.Empty);
            html = EventAttributes().Replace(html, string.Empty);
            return JavascriptUrls().Replace(html, "$1=$2about:blank$2");
        }

        /// <summary>The block the compose pane puts in the body: delimiter plus the readable signature.</summary>
        public static string PlainBlock(string signature) =>
            signature.Length == 0 ? string.Empty : Delimiter + ToPlainText(signature);

        /// <summary>
        /// The message's two bodies. The html part is null when the message should go out as plain
        /// text alone: no signature configured, or the user deleted the signature from this one
        /// message, in which case adding an html part would put back what they removed.
        /// </summary>
        public static (string Text, string? Html) Compose(string body, string signature)
        {
            string text = body.Replace("\r\n", "\n").Replace('\r', '\n');
            if (signature.Length == 0) return (text, null);

            string block = PlainBlock(signature);
            int at = text.IndexOf(block, StringComparison.Ordinal);
            if (at < 0) return (text, null);

            string before = text[..at];
            string after = text[(at + block.Length)..];
            string html = TextToHtml(before)
                + $"<div style=\"margin-top:1em\">{ToOutgoingHtml(signature)}</div>"
                + TextToHtml(after);
            return (text, html);
        }

        /// <summary>Plain text as an html fragment: escaped, with the line breaks kept.</summary>
        public static string TextToHtml(string text) =>
            WebUtility.HtmlEncode(text).Replace("\n", "<br>\n");

        /// <summary>
        /// A whole document for the settings preview frame. Rendered with
        /// <c>sandbox="allow-scripts"</c> and WITHOUT allow-same-origin: the signature is the
        /// user's own code so its script and style run, but it runs in an opaque origin where it
        /// can reach neither the app nor any of their data.
        /// </summary>
        public static string PreviewDocument(string signature) =>
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>"
            + $"html,body{{margin:0;padding:12px;background:{AppColors.MailCanvas.Background};color:{AppColors.MailCanvas.Text};"
            + "font-family:'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:14px;line-height:1.55;}"
            + $"a{{color:{AppColors.MailCanvas.Link};}}img{{max-width:100%;height:auto;}}"
            + $"</style></head><body>{ToHtml(signature)}</body></html>";
    }
}
