using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Sends outgoing mail over the account's SMTP endpoint through the central resilience pipeline,
    /// or directly to the recipient's MX servers over Tor when the account has no SMTP relay
    /// (such as Fast Mail accounts or Direct-to-MX Tor accounts).
    /// </summary>
    public static class SmtpSendService
    {
        public static async Task SendAsync(MailAccountData account, MimeMessage message, CancellationToken cancellationToken = default)
        {
            if (message.From.Count == 0)
                message.From.Add(new MailboxAddress(account.DisplayName, account.EmailAddress));

            if (account.Protocol == IncomingProtocol.FastMail || string.IsNullOrWhiteSpace(account.SmtpHost))
            {
                Log($"Direct-to-MX over Tor send started: {account.EmailAddress} -> {string.Join(", ", message.To.Mailboxes.Select(m => m.Address))}");
                await DirectMxSmtpService.SendAsync(account, message, cancellationToken);
                Log($"Direct-to-MX over Tor send finished: '{message.Subject}'");
                return;
            }

            string? anonymousDomain = account.TorOnly
                ? DirectMxSmtpService.SanitizeMessageForAnonymity(account, message)
                : null;

            Log($"SMTP send started: {account.EmailAddress} -> {string.Join(", ", message.To.Mailboxes.Select(m => m.Address))}");
            await ResiliencePolicy.RunNetwork(async ct =>
            {
                using var client = await MailConnections.OpenSmtpAsync(account, ct);
                if (!string.IsNullOrWhiteSpace(anonymousDomain))
                    client.LocalDomain = anonymousDomain;
                await client.SendAsync(message, ct);
                await client.DisconnectAsync(true, ct);
            }, cancellationToken, account);
            Log($"SMTP send finished: '{message.Subject}'");
        }
    }
}
