using System.Text.RegularExpressions;
using MailKit;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Builds reply/forward drafts from cached MIME and sends drafts: SMTP first, then a
    /// best-effort copy into the account's Sent folder (IMAP append, or a local Sent folder for
    /// POP3) so outgoing mail shows up in the app without waiting for a full resync.
    /// </summary>
    public static class ComposeService
    {
        public const string LocalSentFolderName = "Sent";

        public static ComposeDraft BuildNew(MailAccountData account) => new() { Account = account };

        public static ComposeDraft BuildReply(MailAccountData account, string folderFullName, MailMessageSummary summary, bool replyAll)
        {
            var draft = new ComposeDraft
            {
                Account = account,
                To = summary.FromAddress,
                Subject = summary.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? summary.Subject : $"Re: {summary.Subject}",
                Body = QuoteBody(account, folderFullName, summary)
            };

            if (replyAll)
            {
                var others = summary.ToAddresses
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(address => !address.Equals(account.EmailAddress, StringComparison.OrdinalIgnoreCase));
                draft.Cc = string.Join(", ", others);
            }
            return draft;
        }

        public static ComposeDraft BuildForward(MailAccountData account, string folderFullName, MailMessageSummary summary) => new()
        {
            Account = account,
            Subject = summary.Subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) ? summary.Subject : $"Fwd: {summary.Subject}",
            Body = QuoteBody(account, folderFullName, summary)
        };

        /// <summary>"On (date), (sender) wrote:" header plus the original body prefixed with "&gt; ".</summary>
        static string QuoteBody(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            string original = LoadPlainBody(account, folderFullName, summary);
            string quoted = string.Join("\n", original.Split('\n').Select(static line => "> " + line.TrimEnd('\r')));
            return $"\n\nOn {summary.DateUtc.ToLocalTime():yyyy-MM-dd HH:mm}, {summary.FromName} <{summary.FromAddress}> wrote:\n{quoted}";
        }

        /// <summary>The cached message's text body; falls back to tag-stripped HTML, then to the stored preview.</summary>
        public static string LoadPlainBody(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            try
            {
                byte[]? mimeBytes = MessageStore.TryLoadFullMessage(account.Id, folderFullName, summary.Uid);
                if (mimeBytes != null)
                {
                    using var stream = new MemoryStream(mimeBytes);
                    var message = MimeMessage.Load(stream);
                    if (!string.IsNullOrWhiteSpace(message.TextBody)) return message.TextBody;
                    if (!string.IsNullOrWhiteSpace(message.HtmlBody))
                        return Regex.Replace(message.HtmlBody, "<[^>]+>", " ").Trim();
                }
            }
            catch (Exception ex)
            {
                Log($"Could not load cached body for quoting: {ex.Message}", LogLevel.Warning);
            }
            return summary.PreviewText;
        }

        /// <summary>Sends the draft and archives a copy to Sent. Throws with a readable message on invalid addresses.</summary>
        public static async Task SendAsync(ComposeDraft draft, CancellationToken cancellationToken = default)
        {
            var account = draft.Account ?? throw new InvalidOperationException("The draft has no sending account.");

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(account.DisplayName, account.EmailAddress));
            message.To.AddRange(InternetAddressList.Parse(draft.To));
            if (!string.IsNullOrWhiteSpace(draft.Cc))
                message.Cc.AddRange(InternetAddressList.Parse(draft.Cc));
            message.Subject = draft.Subject;
            message.Body = new TextPart("plain") { Text = draft.Body };

            await SmtpSendService.SendAsync(account, message, cancellationToken);
            await ArchiveToSentAsync(account, message, cancellationToken);
        }

        static async Task ArchiveToSentAsync(MailAccountData account, MimeMessage message, CancellationToken cancellationToken)
        {
            try
            {
                if (account.Protocol == IncomingProtocol.Imap)
                {
                    var sentFolder = MessageStore.GetFolders(account.Id).FirstOrDefault(f => f.Role == FolderRole.Sent);
                    if (sentFolder != null)
                    {
                        await ResiliencePolicy.RunNetwork(async ct =>
                        {
                            using var client = await MailConnections.OpenImapAsync(account, ct);
                            var folder = await client.GetFolderAsync(sentFolder.FullName, ct);
                            await folder.AppendAsync(message, MessageFlags.Seen, ct);
                            await client.DisconnectAsync(true, ct);
                        }, cancellationToken);
                        return;
                    }
                }

                // POP3 (or IMAP without a known Sent folder): keep the copy in a local Sent folder.
                using var buffer = new MemoryStream();
                await message.WriteToAsync(buffer, cancellationToken);
                uint uid = Pop3Service.Fnv1aHash(message.MessageId ?? Guid.NewGuid().ToString());
                string localSent = MessageStore.LocalFolderPrefix + LocalSentFolderName;

                if (!MessageStore.GetFolders(account.Id).Any(f => f.FullName == localSent))
                    MessageStore.SaveFolder(new MailFolderData
                    {
                        AccountId = account.Id,
                        FullName = localSent,
                        DisplayName = LocalSentFolderName,
                        Role = FolderRole.Sent,
                        IsLocal = true
                    });

                MessageStore.SaveFullMessage(account.Id, localSent, uid, buffer.ToArray());
                MessageStore.UpsertSummaries(account.Id, localSent,
                [
                    new MailMessageSummary
                    {
                        Uid = uid,
                        MessageId = message.MessageId ?? string.Empty,
                        Subject = message.Subject ?? string.Empty,
                        FromName = account.DisplayName,
                        FromAddress = account.EmailAddress,
                        ToAddresses = string.Join(", ", message.To.Mailboxes.Select(m => m.Address)),
                        DateUtc = DateTime.UtcNow,
                        Flags = MailFlags.Seen,
                        PreviewText = message.TextBody?.Length > 160 ? message.TextBody[..160] : message.TextBody ?? string.Empty
                    }
                ]);
            }
            catch (Exception ex)
            {
                Log($"Sent-copy archive failed (message was still sent): {ex.Message}", LogLevel.Warning);
            }
        }
    }
}
