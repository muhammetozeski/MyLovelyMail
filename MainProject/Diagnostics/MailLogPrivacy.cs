using System.Text.RegularExpressions;

/// <summary>Removes mail addresses, IPs and credential fields before logs reach any output sink.</summary>
internal static partial class MailLogPrivacy
{
    public static string? Redact(string? text)
    {
        if (text == null) return null;
        text = SecretFields().Replace(text, "$1=[redacted]");
        text = EmailAddresses().Replace(text, "[mail address]");
        text = IpAddresses().Replace(text, "[IP address]");
        return text;
    }

    [GeneratedRegex(@"(?i)\b(sid_token|access_token|refresh_token|password|authorization|subject|desiredUser|user)\s*[:=]\s*(?:'[^']*'|""[^""]*""|[^\s&,;]+)")]
    private static partial Regex SecretFields();

    [GeneratedRegex(@"[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}.\-]+")]
    private static partial Regex EmailAddresses();

    [GeneratedRegex(@"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])|\b(?:[a-fA-F0-9]{1,4}:){2,}[a-fA-F0-9:]*\b")]
    private static partial Regex IpAddresses();
}
