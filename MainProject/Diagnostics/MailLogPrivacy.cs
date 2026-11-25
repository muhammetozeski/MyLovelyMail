using System.Text.RegularExpressions;

/// <summary>Removes mail addresses, IPs and credential fields before logs reach any output sink.</summary>
public static partial class MailLogPrivacy
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

    [GeneratedRegex(@"(?<![\w.\\/-])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\w.-])|(?<![\w:])(?:(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}|(?:[0-9a-fA-F]{1,4}:){1,7}:|:(?::[0-9a-fA-F]{1,4}){1,7}|(?:[0-9a-fA-F]{1,4}:){1,6}:[0-9a-fA-F]{1,4})(?![\w:])")]
    private static partial Regex IpAddresses();
}
