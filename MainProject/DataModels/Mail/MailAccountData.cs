using MyLovelyMail.MainProject.Constants.ThemeConstants;

namespace MyLovelyMail.MainProject.DataModels.Mail
{
    /// <summary>Protocol used to receive mail for an account.</summary>
    public enum IncomingProtocol
    {
        Imap,
        Pop3
    }

    /// <summary>The single home of protocol display strings, so UI text always follows the selected enum value.</summary>
    public static class IncomingProtocolNames
    {
        /// <summary>Human-facing name: "IMAP" / "POP3" (the enum members render as "Imap"/"Pop3").</summary>
        public static string DisplayName(this IncomingProtocol protocol) =>
            protocol == IncomingProtocol.Imap ? "IMAP" : "POP3";

        /// <summary>Conventional incoming-host prefix: "imap" / "pop".</summary>
        public static string HostPrefix(this IncomingProtocol protocol) =>
            protocol == IncomingProtocol.Imap ? "imap" : "pop";
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

        /// <summary>
        /// This mailbox is reachable ONLY through Tor. Every IMAP/POP3/SMTP connection is opened
        /// through the Tor SOCKS proxy, the server name is resolved by Tor rather than by this
        /// machine, and remote content in its mail is never fetched. When no Tor route exists the
        /// account fails to connect — it is never downgraded to a direct connection, because the
        /// whole point of the flag is that this address must not be seen leaving this machine.
        /// The server itself stays an ordinary one; only the route to it changes.
        /// </summary>
        public bool TorOnly { get; set; }

        /// <summary>Accent color of this account in the UI (folder dot, avatar ring).</summary>
        public string ColorHex { get; set; } = AppColors.IdentityPalette[0];

        /// <summary>Disabled accounts stay configured but are skipped by sync.</summary>
        public bool Enabled { get; set; } = true;

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Position in the account rail, and therefore which mailbox answers Ctrl+1..9. 0 means
        /// "never ordered"; <see cref="AccountStore.Load"/> fills it from the signup order so
        /// nothing moves the first time.
        /// </summary>
        public int SortOrder { get; set; }
    }
}
