namespace MyLovelyMail.MainProject.DataModels.Mail
{
    /// <summary>Protocol used to receive mail for an account.</summary>
    public enum IncomingProtocol
    {
        Imap,
        Pop3
    }

    /// <summary>Socket security for a mail server connection.</summary>
    public enum ConnectionSecurity
    {
        Auto,
        SslOnConnect,
        StartTls,
        None
    }

    /// <summary>How the account authenticates against its servers.</summary>
    public enum MailAuthMethod
    {
        Password,
        OAuth2
    }

    /// <summary>
    /// One configured mail account: identity plus server endpoints. Passwords are NOT stored here —
    /// they live in the credential vault keyed by <see cref="Id"/>. Persisted as
    /// <c>UserData/Accounts/&lt;Id&gt;/account.json</c>; the per-account settings overrides sit next
    /// to it (see <see cref="Stores.AccountSettings"/>).
    /// </summary>
    public class MailAccountData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string DisplayName { get; set; } = string.Empty;
        public string EmailAddress { get; set; } = string.Empty;

        public IncomingProtocol Protocol { get; set; } = IncomingProtocol.Imap;
        public string IncomingHost { get; set; } = string.Empty;
        public int IncomingPort { get; set; } = 993;
        public ConnectionSecurity IncomingSecurity { get; set; } = ConnectionSecurity.SslOnConnect;
        public string IncomingUsername { get; set; } = string.Empty;

        public string SmtpHost { get; set; } = string.Empty;
        public int SmtpPort { get; set; } = 465;
        public ConnectionSecurity SmtpSecurity { get; set; } = ConnectionSecurity.SslOnConnect;

        /// <summary>Empty means: reuse <see cref="IncomingUsername"/> and its vault password for SMTP.</summary>
        public string SmtpUsername { get; set; } = string.Empty;

        public MailAuthMethod AuthMethod { get; set; } = MailAuthMethod.Password;

        /// <summary>Accent color of this account in the UI (folder dot, avatar ring).</summary>
        public string ColorHex { get; set; } = "#EC6FA9";

        /// <summary>Disabled accounts stay configured but are skipped by sync.</summary>
        public bool Enabled { get; set; } = true;

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }
}
