using MyLovelyMail.MainProject.DataModels.Mail;

namespace MyLovelyMail.MainProject.Constants
{
    /// <summary>One well-known provider's server endpoints, used to prefill the account wizard.</summary>
    public sealed record ProviderPreset(
        string Name,
        string Emoji,
        string ImapHost, int ImapPort, ConnectionSecurity ImapSecurity,
        string Pop3Host, int Pop3Port, ConnectionSecurity Pop3Security,
        string SmtpHost, int SmtpPort, ConnectionSecurity SmtpSecurity,
        string Hint = "");

    /// <summary>Server presets for the popular providers plus a blank Custom entry.</summary>
    public static class ProviderPresets
    {
        public static readonly ProviderPreset Gmail = new(
            "Gmail", "🔴",
            "imap.gmail.com", 993, ConnectionSecurity.SslOnConnect,
            "pop.gmail.com", 995, ConnectionSecurity.SslOnConnect,
            "smtp.gmail.com", 465, ConnectionSecurity.SslOnConnect,
            "Gmail needs an app password: myaccount.google.com → Security → App passwords.");

        public static readonly ProviderPreset Outlook = new(
            "Outlook / Hotmail", "🔵",
            "outlook.office365.com", 993, ConnectionSecurity.SslOnConnect,
            "outlook.office365.com", 995, ConnectionSecurity.SslOnConnect,
            "smtp-mail.outlook.com", 587, ConnectionSecurity.StartTls,
            "Microsoft accounts may require an app password when two-step verification is on.");

        public static readonly ProviderPreset Yahoo = new(
            "Yahoo", "🟣",
            "imap.mail.yahoo.com", 993, ConnectionSecurity.SslOnConnect,
            "pop.mail.yahoo.com", 995, ConnectionSecurity.SslOnConnect,
            "smtp.mail.yahoo.com", 465, ConnectionSecurity.SslOnConnect,
            "Yahoo requires an app password generated in account security settings.");

        public static readonly ProviderPreset ICloud = new(
            "iCloud", "⚪",
            "imap.mail.me.com", 993, ConnectionSecurity.SslOnConnect,
            "", 0, ConnectionSecurity.SslOnConnect,
            "smtp.mail.me.com", 587, ConnectionSecurity.StartTls,
            "iCloud requires an app-specific password from appleid.apple.com. No POP3 support.");

        public static readonly ProviderPreset Yandex = new(
            "Yandex", "🟡",
            "imap.yandex.com", 993, ConnectionSecurity.SslOnConnect,
            "pop.yandex.com", 995, ConnectionSecurity.SslOnConnect,
            "smtp.yandex.com", 465, ConnectionSecurity.SslOnConnect);

        public static readonly ProviderPreset CockLi = new(
            "Cock.li", "🐓",
            "mail.cock.li", 143, ConnectionSecurity.StartTls,
            "", 0, ConnectionSecurity.StartTls,
            "mail.cock.li", 587, ConnectionSecurity.StartTls);

        public static readonly ProviderPreset Custom = new(
            "Custom", "🛠️",
            "", 993, ConnectionSecurity.SslOnConnect,
            "", 995, ConnectionSecurity.SslOnConnect,
            "", 465, ConnectionSecurity.SslOnConnect);

        public static readonly ProviderPreset[] All = [Gmail, Outlook, Yahoo, ICloud, Yandex, CockLi, Custom];

        /// <summary>Guesses the preset from the mail address domain (null → Custom is a safe pick).</summary>
        public static ProviderPreset? GuessFromEmail(string? email)
        {
            int atIndex = email?.IndexOf('@') ?? -1;
            if (email == null || atIndex < 0) return null;
            return email[(atIndex + 1)..].ToLowerInvariant() switch
            {
                "gmail.com" or "googlemail.com" => Gmail,
                "outlook.com" or "hotmail.com" or "live.com" or "msn.com" => Outlook,
                "yahoo.com" or "ymail.com" => Yahoo,
                "icloud.com" or "me.com" or "mac.com" => ICloud,
                "yandex.com" or "yandex.ru" => Yandex,
                "cock.li" => CockLi,
                _ => null
            };
        }
    }
}
