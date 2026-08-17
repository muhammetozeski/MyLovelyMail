using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>Sends outgoing mail over the account's SMTP endpoint through the central resilience pipeline.</summary>
    public static class SmtpSendService
    {
        public static async Task SendAsync(MailAccountData account, MimeMessage message, CancellationToken cancellationToken = default)
        {
            if (message.From.Count == 0)
                message.From.Add(new MailboxAddress(account.DisplayName, account.EmailAddress));

            Log($"SMTP send started: {account.EmailAddress} -> {string.Join(", ", message.To.Mailboxes.Select(m => m.Address))}");
            await ResiliencePolicy.RunNetwork(async ct =>
            {
                using var client = await MailConnections.OpenSmtpAsync(account, ct);
                await client.SendAsync(message, ct);
                await client.DisconnectAsync(true, ct);
            }, cancellationToken);
            Log($"SMTP send finished: '{message.Subject}'");
        }
    }
}
