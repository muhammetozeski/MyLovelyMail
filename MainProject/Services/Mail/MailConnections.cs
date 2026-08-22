using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Security;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Builds connected + authenticated MailKit clients from a <see cref="MailAccountData"/>,
    /// pulling the password from <see cref="CredentialVault"/>. One place maps
    /// <see cref="ConnectionSecurity"/> to MailKit's <see cref="SecureSocketOptions"/>.
    /// </summary>
    public static class MailConnections
    {
        static SecureSocketOptions ToSocketOptions(ConnectionSecurity security) => security switch
        {
            ConnectionSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            ConnectionSecurity.StartTls => SecureSocketOptions.StartTls,
            ConnectionSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto
        };

        /// <summary>The vault password for the account, or an explanatory exception when locked/missing.</summary>
        static string RequirePassword(MailAccountData account, string? passwordOverride)
        {
            string? password = passwordOverride ?? CredentialVault.GetPassword(account.Id);
            if (password == null)
                throw new InvalidOperationException(CredentialVault.IsUnlocked
                    ? $"No password stored for account '{account.EmailAddress}'."
                    : "The credential vault is locked. Unlock it first.");
            return password;
        }

        /// <summary>
        /// Connects and authenticates one MailKit client (the shared body of the three Open*Async
        /// methods). On any failure the half-open client is disposed before the exception continues,
        /// so no caller ever receives or leaks a broken connection.
        /// </summary>
        static async Task<TClient> OpenAsync<TClient>(TClient client, string protocolName, string host, int port,
            ConnectionSecurity security, string username, MailAccountData account, string? passwordOverride,
            CancellationToken cancellationToken) where TClient : MailService
        {
            try
            {
                await client.ConnectAsync(host, port, ToSocketOptions(security), cancellationToken);
                await client.AuthenticateAsync(username, RequirePassword(account, passwordOverride), cancellationToken);
                Log($"{protocolName} connected: {host}:{port}");
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public static Task<ImapClient> OpenImapAsync(MailAccountData account, CancellationToken cancellationToken, string? passwordOverride = null) =>
            OpenAsync(new ImapClient(), "IMAP", account.IncomingHost, account.IncomingPort, account.IncomingSecurity,
                account.IncomingUsername, account, passwordOverride, cancellationToken);

        public static Task<Pop3Client> OpenPop3Async(MailAccountData account, CancellationToken cancellationToken, string? passwordOverride = null) =>
            OpenAsync(new Pop3Client(), "POP3", account.IncomingHost, account.IncomingPort, account.IncomingSecurity,
                account.IncomingUsername, account, passwordOverride, cancellationToken);

        public static Task<SmtpClient> OpenSmtpAsync(MailAccountData account, CancellationToken cancellationToken, string? passwordOverride = null) =>
            OpenAsync(new SmtpClient(), "SMTP", account.SmtpHost, account.SmtpPort, account.SmtpSecurity,
                string.IsNullOrWhiteSpace(account.SmtpUsername) ? account.IncomingUsername : account.SmtpUsername,
                account, passwordOverride, cancellationToken);
    }
}
