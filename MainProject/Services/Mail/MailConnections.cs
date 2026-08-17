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
        public static SecureSocketOptions ToSocketOptions(ConnectionSecurity security) => security switch
        {
            ConnectionSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            ConnectionSecurity.StartTls => SecureSocketOptions.StartTls,
            ConnectionSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto
        };

        /// <summary>The vault password for the account, or an explanatory exception when locked/missing.</summary>
        static string RequirePassword(MailAccountData account)
        {
            string? password = CredentialVault.GetPassword(account.Id);
            if (password == null)
                throw new InvalidOperationException(CredentialVault.IsUnlocked
                    ? $"No password stored for account '{account.EmailAddress}'."
                    : "The credential vault is locked. Unlock it first.");
            return password;
        }

        public static async Task<ImapClient> OpenImapAsync(MailAccountData account, CancellationToken cancellationToken)
        {
            var client = new ImapClient();
            try
            {
                await client.ConnectAsync(account.IncomingHost, account.IncomingPort, ToSocketOptions(account.IncomingSecurity), cancellationToken);
                await client.AuthenticateAsync(account.IncomingUsername, RequirePassword(account), cancellationToken);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public static async Task<Pop3Client> OpenPop3Async(MailAccountData account, CancellationToken cancellationToken)
        {
            var client = new Pop3Client();
            try
            {
                await client.ConnectAsync(account.IncomingHost, account.IncomingPort, ToSocketOptions(account.IncomingSecurity), cancellationToken);
                await client.AuthenticateAsync(account.IncomingUsername, RequirePassword(account), cancellationToken);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public static async Task<SmtpClient> OpenSmtpAsync(MailAccountData account, CancellationToken cancellationToken)
        {
            var client = new SmtpClient();
            try
            {
                string username = string.IsNullOrWhiteSpace(account.SmtpUsername) ? account.IncomingUsername : account.SmtpUsername;
                await client.ConnectAsync(account.SmtpHost, account.SmtpPort, ToSocketOptions(account.SmtpSecurity), cancellationToken);
                await client.AuthenticateAsync(username, RequirePassword(account), cancellationToken);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }
}
