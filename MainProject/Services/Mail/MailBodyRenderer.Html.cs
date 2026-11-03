using System.Net;
using System.Text;
using MimeKit;
using MimeKit.Text;

namespace MyLovelyMail.MainProject.Services.Mail;

public static partial class MailBodyRenderer
{
    /// <summary>Uses the same passive document boundary for an anonymous account's signature preview.</summary>
    public static string RenderAnonymousHtml(string html)
    {
        using var message = new MimeMessage();
        bool blocked = false;
        return WrapDocument(SanitizeHtml(html, message, true, true, ref blocked), true, true);
    }

    static readonly HashSet<string> PassiveTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "address", "article", "b", "bdi", "bdo", "big", "blockquote", "br",
        "caption", "center", "cite", "code", "col", "colgroup", "dd", "del", "details",
        "dfn", "div", "dl", "dt", "em", "figcaption", "figure", "font", "footer",
        "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "i", "img", "ins", "kbd",
        "li", "main", "mark", "ol", "p", "pre", "q", "s", "samp", "section", "small",
        "span", "strike", "strong", "sub", "summary", "sup", "table", "tbody", "td",
        "tfoot", "th", "thead", "time", "tr", "tt", "u", "ul", "var", "wbr"
    };

    static readonly HashSet<string> PassiveAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "abbr", "align", "alt", "bgcolor", "border", "cellpadding", "cellspacing", "class",
        "color", "colspan", "dir", "face", "height", "id", "lang", "rowspan", "scope",
        "size", "span", "start", "title", "type", "valign", "width"
    };

    static readonly HashSet<string> PassiveCssProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "color", "background-color", "font", "font-family", "font-size", "font-style", "font-weight",
        "line-height", "letter-spacing", "text-align", "text-decoration", "text-indent", "text-transform",
        "white-space", "word-break", "word-spacing", "overflow-wrap", "vertical-align", "display",
        "width", "height", "min-width", "max-width", "min-height", "max-height",
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "border", "border-top", "border-right", "border-bottom", "border-left",
        "border-color", "border-style", "border-width", "border-collapse", "border-spacing", "border-radius"
    };

    /// <summary>
    /// Rebuilds passive markup with MimeKit's HTML tokenizer. No original tag or attribute is
    /// copied verbatim, including encoded URLs, truncated tags and foreign SVG/MathML markup.
    /// Anonymous links remain copyable data, without a navigable href in readers or exports.
    /// </summary>
    static string SanitizeHtml(string html, MimeMessage message, bool blockRemote, bool anonymous, ref bool blocked)
    {
        using var input = new StringReader(html);
        var tokenizer = new HtmlTokenizer(input) { DecodeCharacterReferences = true };
        var output = new StringBuilder(html.Length);
        string? suppressed = null;
        bool inStyle = false;
        while (tokenizer.ReadNextToken(out var token))
        {
            if (token is HtmlTagToken tag)
            {
                string name = tag.Name.ToLowerInvariant();
                if (suppressed != null)
                {
                    if (tag.IsEndTag && name == suppressed) suppressed = null;
                    continue;
                }
                if (name == "script" || (name == "style" && blockRemote))
                {
                    if (!tag.IsEndTag) suppressed = name;
                    continue;
                }
                // A normal account may opt into remote styles with remote images. Its sandbox
                // and CSP still forbid scripts and frames. Anonymous mail never takes this path.
                if (name == "style" && !blockRemote && !tag.IsEmptyElement)
                {
                    inStyle = !tag.IsEndTag;
                    output.Append(tag.IsEndTag ? "</style>" : "<style>");
                    continue;
                }
                if (!PassiveTags.Contains(name)) continue;
                output.Append('<');
                if (tag.IsEndTag)
                {
                    output.Append('/').Append(name).Append('>');
                    continue;
                }
                output.Append(name);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var attribute in tag.Attributes)
                {
                    string key = attribute.Name.ToLowerInvariant();
                    if (!seen.Add(key)) continue;
                    string value = attribute.Value ?? string.Empty;
                    if (key == "href" && name == "a")
                    {
                        value = value.Trim();
                        if (!Services.ExternalLinkService.IsOpenable(value) && !value.StartsWith('#')) continue;
                        AppendAttribute(output, anonymous ? "data-mlm-link" : "href", value);
                    }
                    else if (key == "src" && name == "img")
                    {
                        string? image = SafeImageSource(value, message, blockRemote);
                        if (image == null)
                        {
                            blocked = true;
                            image = BlockedImagePlaceholder;
                        }
                        AppendAttribute(output, "src", image);
                    }
                    else if (key == "style")
                    {
                        AppendAttribute(output, key, blockRemote ? PassiveStyle(value) : value);
                    }
                    else if (PassiveAttributes.Contains(key))
                    {
                        AppendAttribute(output, key, value);
                    }
                }
                if (name == "a") AppendAttribute(output, "rel", "noreferrer noopener");
                output.Append('>');
            }
            else if (suppressed == null && token is HtmlDataToken data)
            {
                output.Append(inStyle ? data.Data : WebUtility.HtmlEncode(data.Data));
            }
        }
        return output.ToString();
    }

    static void AppendAttribute(StringBuilder output, string name, string value) =>
        output.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');

    static string PassiveStyle(string style)
    {
        var result = new StringBuilder();
        foreach (string declaration in style.Split(';'))
        {
            int colon = declaration.IndexOf(':');
            if (colon < 1) continue;
            string property = declaration[..colon].Trim();
            string value = declaration[(colon + 1)..].Trim();
            if (!PassiveCssProperties.Contains(property) || value.Length == 0) continue;
            // Escapes, comments, at-rules and URLs cannot be smuggled into the allowed values.
            if (value.Any(static c => !(char.IsAsciiLetterOrDigit(c) || char.IsWhiteSpace(c) || "#.,%()+-!/'\"".Contains(c)))
                || value.Contains("url", StringComparison.OrdinalIgnoreCase)
                || value.Contains("expression", StringComparison.OrdinalIgnoreCase)
                || value.Contains("/*", StringComparison.Ordinal)) continue;
            result.Append(property).Append(':').Append(value).Append(';');
        }
        return result.ToString();
    }

    static string? SafeImageSource(string value, MimeMessage message, bool blockRemote)
    {
        value = value.Trim();
        if (value.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
        {
            string id = value[4..].Trim('<', '>');
            var part = message.BodyParts.OfType<MimePart>().FirstOrDefault(p => string.Equals(p.ContentId, id, StringComparison.OrdinalIgnoreCase));
            if (part?.Content == null || !IsRasterImage(part.ContentType.MimeType)) return null;
            using var buffer = new MemoryStream();
            part.Content.DecodeTo(buffer);
            return $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(buffer.ToArray())}";
        }
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int semicolon = value.IndexOf(';');
            if (semicolon > 5 && IsRasterImage(value[5..semicolon])
                && value.AsSpan(semicolon).StartsWith(";base64,", StringComparison.OrdinalIgnoreCase)) return value;
            return null;
        }
        if (!blockRemote && Uri.TryCreate(value.StartsWith("//") ? "https:" + value : value, UriKind.Absolute, out var uri)
            && uri.Scheme is "https" or "http") return uri.AbsoluteUri;
        return null;
    }

    static bool IsRasterImage(string mimeType) => mimeType.ToLowerInvariant() is
        "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp" or "image/avif";
}
