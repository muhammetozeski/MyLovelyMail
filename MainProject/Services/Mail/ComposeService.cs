using System.Net.Sockets;
using System.Text.RegularExpressions;
using MailKit;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using Polly.Timeout;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>What SendAsync actually did: submitted over SMTP, or parked in the local Outbox for the next flush.</summary>
    public enum SendOutcome { Sent, QueuedOffline }

    /// <summary>
    /// Builds reply/forward drafts from cached MIME and sends drafts: SMTP first, then a
    /// best-effort copy into the account's Sent folder (IMAP append, or a local Sent folder for
    /// POP3) so outgoing mail shows up in the app without waiting for a full resync.
    /// </summary>
    public static class ComposeService
    {
        const string LocalSentFolderName = "Sent";
        const string LocalDraftsFolderName = "Drafts";
        public const string LocalOutboxFolderName = "Outbox";

        /// <summary>Raw recipient text survives in headers even when it is not yet a parseable address.</summary>
        const string DraftToHeader = "X-LovelyDraft-To";
        const string DraftCcHeader = "X-LovelyDraft-Cc";
        /// <summary>'|'-separated staged attachment paths, so a reopened draft gets its files back.</summary>
        const string DraftAttachmentsHeader = "X-LovelyDraft-Attachments";
        const string DraftMessageIdPrefix = "draft:";

        /// <summary>Attachment picks are copied here (per draft id) so sending never depends on the original file still existing.</summary>
        static string AttachmentStageFolder(ComposeDraft draft) =>
            Path.Combine(AppPaths.AppCache, "ComposeAttachments", draft.DraftId);

        static string LocalDraftsFullName => MessageStore.LocalFolderPrefix + LocalDraftsFolderName;

        static uint DraftUid(ComposeDraft draft) => Pop3Service.Fnv1aHash(DraftMessageIdPrefix + draft.DraftId);

        /// <summary>Writes/overwrites the draft in the local Drafts folder (autosave + close paths).</summary>
        public static void SaveDraft(ComposeDraft draft)
        {
            if (draft.Account is not { } account) return;

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(account.DisplayName, account.EmailAddress));
            message.Subject = draft.Subject;
            message.Headers.Add(DraftToHeader, draft.To);
            message.Headers.Add(DraftCcHeader, draft.Cc);
            if (draft.AttachmentPaths.Count > 0)
                message.Headers.Add(DraftAttachmentsHeader, string.Join('|', draft.AttachmentPaths));
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
            if (MessageStore.TryLoadMimeMessage(account.Id, LocalDraftsFullName, summary.Uid) is not { } message)
                return null;

            return new ComposeDraft
            {
                DraftId = summary.MessageId.StartsWith(DraftMessageIdPrefix) ? summary.MessageId[DraftMessageIdPrefix.Length..] : Guid.NewGuid().ToString("N"),
                Account = account,
                To = message.Headers[DraftToHeader] ?? string.Join(", ", message.To.Mailboxes.Select(static m => m.Address)),
                Cc = message.Headers[DraftCcHeader] ?? string.Empty,
                Subject = message.Subject ?? string.Empty,
                Body = message.TextBody ?? string.Empty,
                // Only files still present in the stage folder come back; the rest are silently gone.
                AttachmentPaths = [.. (message.Headers[DraftAttachmentsHeader] ?? string.Empty)
                    .Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Where(File.Exists)]
            };
        }

        public static void DeleteDraft(ComposeDraft draft)
        {
            if (draft.Account is { } account)
                MessageStore.RemoveMessages(account.Id, LocalDraftsFullName, [DraftUid(draft)]);
            try
            {
                if (Directory.Exists(AttachmentStageFolder(draft)))
                    Directory.Delete(AttachmentStageFolder(draft), recursive: true);
            }
            catch (Exception ex)
            {
                Log($"Could not clean staged attachments: {ex.Message}", LogLevel.Warning);
            }
        }

        /// <summary>Copies a picked file into the draft's stage folder and records it on the draft.</summary>
        public static async Task AttachFileAsync(ComposeDraft draft, Stream source, string fileName)
        {
            string folder = AttachmentStageFolder(draft);
            Directory.CreateDirectory(folder);
            string target = AttachmentService.UniquePath(folder, fileName);

            await using (var output = File.Create(target))
                await source.CopyToAsync(output);
            draft.AttachmentPaths.Add(target);
        }

        public static void RemoveAttachment(ComposeDraft draft, string path)
        {
            draft.AttachmentPaths.Remove(path);
            try { File.Delete(path); } catch { /* stage cleanup is best-effort */ }
        }

        /// <summary>RFC 3676 signature delimiter; mail clients fold everything under it.</summary>
        const string SignatureDelimiter = "\n\n-- \n";

        /// <summary>The account's signature block, or empty when no signature is configured.</summary>
        static string SignatureBlock(MailAccountData account)
        {
            string signature = AccountStore.GetSettings(account.Id).Signature.Value;
            return signature.Length == 0 ? string.Empty : SignatureDelimiter + signature;
        }

        public static ComposeDraft BuildNew(MailAccountData account) =>
            new() { Account = account, Body = SignatureBlock(account) };

        public static ComposeDraft BuildReply(MailAccountData account, string folderFullName, MailMessageSummary summary, bool replyAll)
        {
            var draft = new ComposeDraft
            {
                Account = account,
                To = summary.FromAddress,
                SourceFolder = folderFullName,
                SourceUid = summary.Uid,
                Subject = summary.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? summary.Subject : $"Re: {summary.Subject}",
                // Signature sits ABOVE the quote, where the reply is typed.
                Body = SignatureBlock(account) + QuoteBody(account, folderFullName, summary)
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
            SourceFolder = folderFullName,
            SourceUid = summary.Uid,
            Subject = summary.Subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) ? summary.Subject : $"Fwd: {summary.Subject}",
            Body = SignatureBlock(account) + QuoteBody(account, folderFullName, summary)
        };

        /// <summary>
        /// The attribution line plus as much of the original as the account's quote style asks
        /// for. Every reply used to carry the whole history forward, so a five-round thread
        /// shipped a body several times bigger than what was actually written — and the only way
        /// to shorten it was deleting lines by hand. The reader already folds that history away
        /// behind FoldQuotedText; this is the matching control on the writing side.
        /// </summary>
        public static string QuoteBody(MailAccountData account, string folderFullName, MailMessageSummary summary, QuoteStyle? styleOverride = null)
        {
            var settings = AccountStore.GetSettings(account.Id);
            var style = styleOverride ?? settings.ReplyQuoteStyle.Value;
            string attribution = $"\n\nOn {summary.DateUtc.ToLocalTime():yyyy-MM-dd HH:mm}, {summary.FromName} <{summary.FromAddress}> wrote:";
            if (style == QuoteStyle.None) return attribution;

            string[] lines = LoadPlainBody(account, folderFullName, summary).Split('\n');
            if (style == QuoteStyle.Trimmed)
                lines = TrimToOwnWords(lines, settings.QuoteTrimLines.Value);

            return attribution + "\n" + string.Join("\n", lines.Select(static line => "> " + line.TrimEnd('\r')));
        }

        /// <summary>
        /// Keeps the head of the message and drops the history it was already carrying, replacing
        /// it with one line that still reads as part of the quote. The boundary comes from
        /// <see cref="MailBodyRenderer.PlainQuoteStart"/> — the same definition of "quoted" the
        /// reader fold and the attachment scan use, so the three cannot disagree.
        /// </summary>
        static string[] TrimToOwnWords(string[] lines, int keepLines)
        {
            int cut = lines.Length;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (!MailBodyRenderer.PlainQuoteStart().IsMatch(line) && !line.TrimStart().StartsWith(">>")) continue;
                cut = i;
                break;
            }
            cut = Math.Min(cut, Math.Max(1, keepLines));
            if (cut >= lines.Length) return lines;

            return [.. lines[..cut], $"[... {lines.Length - cut} earlier quoted lines trimmed]"];
        }

        /// <summary>The cached message's text body; falls back to tag-stripped HTML, then to the stored preview.</summary>
        static string LoadPlainBody(MailAccountData account, string folderFullName, MailMessageSummary summary)
        {
            try
            {
                if (MessageStore.TryLoadMimeMessage(account.Id, folderFullName, summary.Uid) is { } message)
                {
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

        /// <summary>
        /// Sends the draft and archives a copy to Sent. A connection-level failure parks the built
        /// message in the local Outbox instead of throwing (the periodic flush retries it);
        /// permanent errors (bad addresses, auth) still throw with a readable message.
        /// </summary>
        public static async Task<SendOutcome> SendAsync(ComposeDraft draft, CancellationToken cancellationToken = default)
        {
            var account = draft.Account ?? throw new InvalidOperationException("The draft has no sending account.");

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(account.DisplayName, account.EmailAddress));
            message.To.AddRange(InternetAddressList.Parse(draft.To));
            if (!string.IsNullOrWhiteSpace(draft.Cc))
                message.Cc.AddRange(InternetAddressList.Parse(draft.Cc));
            message.Subject = draft.Subject;

            var builder = new BodyBuilder { TextBody = draft.Body };
            foreach (string path in draft.AttachmentPaths.Where(File.Exists))
                await builder.Attachments.AddAsync(path, cancellationToken);
            message.Body = builder.ToMessageBody();

            Log($"Compose send started: '{draft.Subject}' -> {draft.To}");
            try
            {
                await SmtpSendService.SendAsync(account, message, cancellationToken);
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                await StoreAsMessageInLocalFolderAsync(account, message, LocalOutboxFolderName, FolderRole.Outbox, cancellationToken);
                DeleteDraft(draft);
                Log($"Offline: '{draft.Subject}' queued to Outbox ({ex.Message}).", LogLevel.Warning);
                return SendOutcome.QueuedOffline;
            }
            await ArchiveToSentAsync(account, message, cancellationToken);
            DeleteDraft(draft);
            Log($"Compose send finished: '{draft.Subject}'");
            return SendOutcome.Sent;
        }

        /// <summary>
        /// Connection-level = worth retrying later. Auth failures and SMTP command rejections are
        /// permanent — retrying those forever would spin, so they surface to the caller instead.
        /// </summary>
        static bool IsConnectionFailure(Exception ex) =>
            ex is SocketException or IOException or TimeoutException
                or ServiceNotConnectedException or TimeoutRejectedException
            || ex.InnerException is SocketException;

        /// <summary>Also used by the outbox flush, which re-sends a stored MimeMessage without a draft.</summary>
        internal static async Task ArchiveToSentAsync(MailAccountData account, MimeMessage message, CancellationToken cancellationToken)
        {
            try
            {
                if (account.Protocol == IncomingProtocol.Imap)
                {
                    var folders = MessageStore.GetFolders(account.Id);
                    // Prefer the marked Sent folder; fall back to the conventional name for servers without SPECIAL-USE.
                    var sentFolder = folders.FirstOrDefault(static f => f.Role == FolderRole.Sent && !f.IsLocal)
                        ?? folders.FirstOrDefault(static f => !f.IsLocal && ImapSyncService.GuessRoleFromName(f.DisplayName) == FolderRole.Sent);
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
                await StoreAsMessageInLocalFolderAsync(account, message, LocalSentFolderName, FolderRole.Sent, cancellationToken);
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

        /// <summary>Serializes a built MimeMessage into an app-local folder with its summary row (Sent archive + Outbox queue).</summary>
        static async Task StoreAsMessageInLocalFolderAsync(MailAccountData account, MimeMessage message, string folderName, FolderRole role, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await message.WriteToAsync(buffer, cancellationToken);
            uint uid = Pop3Service.Fnv1aHash(message.MessageId ?? Guid.NewGuid().ToString());
            StoreInLocalFolder(account, folderName, role, uid, buffer.ToArray(),
                new MailMessageSummary
                {
                    Uid = uid,
                    MessageId = message.MessageId ?? string.Empty,
                    Subject = message.Subject ?? string.Empty,
                    FromName = account.DisplayName,
                    FromAddress = account.EmailAddress,
                    ToAddresses = string.Join(", ", message.To.Mailboxes.Select(static m => m.Address)),
                    DateUtc = DateTime.UtcNow,
                    Flags = MailFlags.Seen,
                    PreviewText = Preview(message.TextBody)
                });
        }

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
