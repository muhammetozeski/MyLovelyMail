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
        public const string LocalDraftsFolderName = "Drafts";

        /// <summary>Raw recipient text survives in headers even when it is not yet a parseable address.</summary>
        const string DraftToHeader = "X-LovelyDraft-To";
        const string DraftCcHeader = "X-LovelyDraft-Cc";
        const string DraftMessageIdPrefix = "draft:";

        static string LocalDraftsFullName => MessageStore.LocalFolderPrefix + LocalDraftsFolderName;

        public static uint DraftUid(ComposeDraft draft) => Pop3Service.Fnv1aHash(DraftMessageIdPrefix + draft.DraftId);

        /// <summary>Writes/overwrites the draft in the local Drafts folder (autosave + close paths).</summary>
        public static void SaveDraft(ComposeDraft draft)
        {
            if (draft.Account is not { } account) return;

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(account.DisplayName, account.EmailAddress));
            message.Subject = draft.Subject;
            message.Headers.Add(DraftToHeader, draft.To);
            message.Headers.Add(DraftCcHeader, draft.Cc);
            message.Body = new TextPart("plain") { Text = draft.Body };

            using var buffer = new MemoryStream();
            message.WriteTo(buffer);
            uint uid = DraftUid(draft);
            StoreInLocalFolder(account, LocalDraftsFolderName, FolderRole.Drafts, uid, buffer.ToArray(),
                new MailMessageSummary
                {
                    Uid = uid,
                    MessageId = DraftMessageIdPrefix + draft.DraftId,
                    Subject = draft.Subject,
                    FromName = account.DisplayName,
                    FromAddress = account.EmailAddress,
                    ToAddresses = draft.To,
                    DateUtc = DateTime.UtcNow,
                    Flags = MailFlags.Draft | MailFlags.Seen,
                    PreviewText = Preview(draft.Body)
                });
        }

        /// <summary>Reopens a stored draft summary as an editable ComposeDraft (null when its MIME is gone).</summary>
        public static ComposeDraft? LoadDraft(MailAccountData account, MailMessageSummary summary)
        {
            byte[]? bytes = MessageStore.TryLoadFullMessage(account.Id, LocalDraftsFullName, summary.Uid);
            if (bytes == null) return null;

            using var stream = new MemoryStream(bytes);
            var message = MimeMessage.Load(stream);
            return new ComposeDraft
            {
                DraftId = summary.MessageId.StartsWith(DraftMessageIdPrefix) ? summary.MessageId[DraftMessageIdPrefix.Length..] : Guid.NewGuid().ToString("N"),
                Account = account,
                To = message.Headers[DraftToHeader] ?? string.Join(", ", message.To.Mailboxes.Select(m => m.Address)),
                Cc = message.Headers[DraftCcHeader] ?? string.Empty,
                Subject = message.Subject ?? string.Empty,
                Body = message.TextBody ?? string.Empty
            };
        }

        public static void DeleteDraft(ComposeDraft draft)
        {
            if (draft.Account is { } account)
                MessageStore.RemoveMessages(account.Id, LocalDraftsFullName, [DraftUid(draft)]);
        }

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

            Log($"Compose send started: '{draft.Subject}' -> {draft.To}");
            await SmtpSendService.SendAsync(account, message, cancellationToken);
            await ArchiveToSentAsync(account, message, cancellationToken);
            DeleteDraft(draft);
            Log($"Compose send finished: '{draft.Subject}'");
        }

        static async Task ArchiveToSentAsync(MailAccountData account, MimeMessage message, CancellationToken cancellationToken)
        {
            try
            {
                if (account.Protocol == IncomingProtocol.Imap)
                {
                    var folders = MessageStore.GetFolders(account.Id);
                    // Prefer the marked Sent folder; fall back to the conventional name for servers without SPECIAL-USE.
                    var sentFolder = folders.FirstOrDefault(f => f.Role == FolderRole.Sent && !f.IsLocal)
                        ?? folders.FirstOrDefault(f => !f.IsLocal && ImapSyncService.GuessRoleFromName(f.DisplayName) == FolderRole.Sent);
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
                StoreInLocalFolder(account, LocalSentFolderName, FolderRole.Sent, uid, buffer.ToArray(),
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
                        PreviewText = Preview(message.TextBody)
                    });
            }
            catch (Exception ex)
            {
                Log($"Sent-copy archive failed (message was still sent): {ex.Message}", LogLevel.Warning);
            }
        }

        /// <summary>List-row preview text: the first <see cref="PreviewChars"/> characters of the body.</summary>
        const int PreviewChars = 160;
        static string Preview(string? body) =>
            body == null ? string.Empty : body.Length > PreviewChars ? body[..PreviewChars] : body;

        /// <summary>
        /// Ensures the app-local folder exists, then stores the MIME plus its summary row —
        /// the shared tail of draft saving and the POP3/no-Sent-folder archive path.
        /// </summary>
        static void StoreInLocalFolder(MailAccountData account, string folderName, FolderRole role, uint uid, byte[] mimeBytes, MailMessageSummary summary)
        {
            string fullName = MessageStore.LocalFolderPrefix + folderName;
            if (!MessageStore.GetFolders(account.Id).Any(f => f.FullName == fullName))
                MessageStore.SaveFolder(new MailFolderData
                {
                    AccountId = account.Id,
                    FullName = fullName,
                    DisplayName = folderName,
                    Role = role,
                    IsLocal = true
                });

            MessageStore.SaveFullMessage(account.Id, fullName, uid, mimeBytes);
            MessageStore.UpsertSummaries(account.Id, fullName, [summary]);
        }
    }
}
