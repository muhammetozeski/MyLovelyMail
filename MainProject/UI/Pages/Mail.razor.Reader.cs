using System.Text;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using GlobalSettings = MyLovelyMail.MainProject.Stores.Settings;

namespace MyLovelyMail.MainProject.UI.Pages
{
    // Reader pane: open-message body, attachments, tags and single-message actions.
    public partial class Mail
    {
        string? OpenBodyHtml { get; set; }
        bool OpenBodyLoading { get; set; }
        bool OpenBodyBlockedImages { get; set; }
        string? BodyError { get; set; }
        uint loadedBodyUid;
        uint remoteImagesAllowedUid;

        List<AttachmentInfo> OpenAttachments { get; set; } = [];
        string? SaveStatus { get; set; }
        bool ShowTagPicker { get; set; }
        string NewTagName { get; set; } = string.Empty;

        /// <summary>Unsubscribe targets of the open message; filled with its body, cleared with it.</summary>
        UnsubscribeTargets? unsubscribeTargets;

        /// <summary>Opens the unsubscribe page in the default browser — never automatically, only from the chip.</summary>
        void OpenUnsubscribePage(string url)
        {
            SaveStatus = ExternalLinkService.TryOpen(url, out string failureReason)
                ? "🌐 Unsubscribe page opened in your browser"
                : $"❌ {failureReason}";
        }

        /// <summary>Prepares the unsubscribe mail as a normal draft — the user still presses Send.</summary>
        void ComposeUnsubscribeMail(UnsubscribeTargets targets)
        {
            if (MailUiState.SelectedAccount is not { } account || targets.MailtoAddress is not { } address) return;
            var draft = ComposeService.BuildNew(account);
            draft.To = address;
            draft.Subject = targets.MailtoSubject ?? "unsubscribe";
            MailUiState.OpenCompose(draft);
        }

        /// <summary>What the app already knows about the open message's sender.</summary>
        sealed record SenderHistory(int MessageCount, int UnreadCount, DateTime OldestUtc);

        SenderHistory? senderHistory;
        uint senderHistoryUid;

        /// <summary>
        /// Counts everything from this sender across the account. The scan walks every cached
        /// summary, so it runs ONCE per opened message (keyed by uid) — never per render, which
        /// the reader does on any state change.
        /// </summary>
        SenderHistory? HistoryOfOpenSender(MailMessageSummary open)
        {
            if (senderHistoryUid == open.Uid) return senderHistory;
            senderHistoryUid = open.Uid;
            senderHistory = null;

            if (MailUiState.SelectedAccount is not { } account || open.FromAddress.Length == 0) return null;

            int count = 0, unread = 0;
            DateTime oldest = DateTime.MaxValue;
            foreach (var folder in MessageStore.GetFolders(account.Id))
            {
                foreach (var summary in MessageStore.GetSummaries(account.Id, folder.FullName))
                {
                    if (!summary.FromAddress.Equals(open.FromAddress, StringComparison.OrdinalIgnoreCase)) continue;
                    count++;
                    if (summary.IsUnread) unread++;
                    if (summary.DateUtc < oldest) oldest = summary.DateUtc;
                }
            }

            senderHistory = count > 1 ? new SenderHistory(count, unread, oldest) : null;
            return senderHistory;
        }

        List<MailMessageSummary>? openThread;
        uint openThreadUid;

        /// <summary>
        /// The conversation the open message belongs to, oldest first, or null when it stands
        /// alone. Cached by uid like <see cref="HistoryOfOpenSender"/>: threading walks the whole
        /// folder, and the reader re-renders on any state change.
        /// <para>
        /// Built from the folder's FULL summaries rather than the filtered list — an active search
        /// would otherwise cut the conversation down to the messages that happen to match it.
        /// </para>
        /// </summary>
        List<MailMessageSummary>? ThreadOfOpenMessage(MailMessageSummary open)
        {
            if (openThreadUid == open.Uid) return openThread;
            openThreadUid = open.Uid;
            openThread = null;

            if (MailUiState.SelectedAccount is not { } account || ResolveFolderOf(open) is not { } folderName)
                return null;

            var thread = ThreadingService.BuildThreads(MessageStore.GetSummaries(account.Id, folderName))
                .FirstOrDefault(t => t.Messages.Any(m => m.Uid == open.Uid));

            // A single message is not a conversation; the strip would be noise on most mail.
            openThread = thread is { Messages.Count: > 1 } ? thread.Messages : null;
            return openThread;
        }

        /// <summary>Position of the open message in its conversation, 1-based, for "3 of 7".</summary>
        int OpenThreadPosition(MailMessageSummary open) =>
            (ThreadOfOpenMessage(open)?.FindIndex(m => m.Uid == open.Uid) ?? -1) + 1;

        /// <summary>
        /// Opens the neighbouring message in the conversation. Returns false at either end, so the
        /// arrows can be disabled rather than silently doing nothing.
        /// </summary>
        bool StepThread(int delta)
        {
            if (MailUiState.OpenMessage is not { } open || ThreadOfOpenMessage(open) is not { } thread) return false;
            int target = thread.FindIndex(m => m.Uid == open.Uid) + delta;
            if (target < 0 || target >= thread.Count) return false;
            OpenMessage(thread[target]);
            return true;
        }

        /// <summary>Re-renders the open message with remote images allowed for THIS message only.</summary>
        void AllowImagesOnce()
        {
            if (MailUiState.OpenMessage is not { } open) return;
            remoteImagesAllowedUid = open.Uid;
            loadedBodyUid = 0;
            _ = LoadOpenBodyIfNeededAsync();
        }

        /// <summary>
        /// Renders the opened message's cached body; downloads it first when missing. A uid guard
        /// drops stale results when the user opens another message mid-download.
        /// </summary>
        async Task LoadOpenBodyIfNeededAsync()
        {
            var account = MailUiState.SelectedAccount;
            var open = MailUiState.OpenMessage;
            if (account == null || open == null)
            {
                OpenBodyHtml = null;
                loadedBodyUid = 0;
                return;
            }
            if (loadedBodyUid == open.Uid && (OpenBodyHtml != null || OpenBodyLoading)) return;
            if (ResolveFolderOf(open) is not { } folderName) return;

            loadedBodyUid = open.Uid;
            OpenBodyHtml = null;
            BodyError = null;
            OpenBodyLoading = true;
            await InvokeAsync(StateHasChanged);

            bool allowRemote = open.Uid == remoteImagesAllowedUid;
            var rendered = MailBodyRenderer.Render(account, folderName, open, allowRemote);
            if (rendered == null)
            {
                try
                {
                    if (account.Protocol == IncomingProtocol.Imap && !folderName.StartsWith(MessageStore.LocalFolderPrefix))
                        await ImapSyncService.DownloadMessageAsync(account, folderName, open.Uid);
                    else if (account.Protocol == IncomingProtocol.Pop3)
                        await Pop3Service.DownloadMessageAsync(account, open.Uid);
                    rendered = MailBodyRenderer.Render(account, folderName, open, allowRemote);
                }
                catch (Exception ex)
                {
                    BodyError = ex.Message;
                }
            }

            if (MailUiState.OpenMessage?.Uid != open.Uid) return;

            OpenBodyHtml = rendered?.Html;
            OpenBodyBlockedImages = rendered?.RemoteImagesBlocked ?? false;
            OpenBodyLoading = false;
            OpenAttachments = open.HasAttachments ? AttachmentService.List(account, folderName, open) : [];
            unsubscribeTargets = UnsubscribeService.Read(account, folderName, open);
            MarkOpenAsRead(account, folderName, open);
            await InvokeAsync(StateHasChanged);
        }

        /// <summary>Applies the opened-equals-read behavior, honoring the configured delay.</summary>
        static void MarkOpenAsRead(MailAccountData account, string folderName, MailMessageSummary open)
        {
            int delaySeconds = GlobalSettings.MarkAsReadDelaySeconds.Value;
            if (delaySeconds <= 0)
            {
                MessageActions.MarkRead(account, folderName, open);
                return;
            }
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                if (MailUiState.OpenMessage?.Uid == open.Uid)
                    MessageActions.MarkRead(account, folderName, open);
            });
        }

        /// <summary>Shared guard of the single-message actions: runs the action with the selected
        /// account and the message's real folder, or does nothing when either is missing.</summary>
        void WithAccountAndFolder(MailMessageSummary message, Action<MailAccountData, string> action)
        {
            if (MailUiState.SelectedAccount is { } account && ResolveFolderOf(message) is { } folderName)
                action(account, folderName);
        }

        void ToggleTagOnOpen(MailMessageSummary message, string tagName) =>
            WithAccountAndFolder(message, (account, folderName) => MessageActions.ToggleTag(account, folderName, message, tagName));

        void AddNewTag(MailMessageSummary message)
        {
            string tagName = NewTagName.Trim();
            if (tagName.Length == 0) return;
            TagStore.ColorOf(tagName); // registers the new tag's chip color before first render
            ToggleTagOnOpen(message, tagName);
            NewTagName = string.Empty;
        }

        /// <summary>Guard-plus-status shell of the save/export buttons: resolves the message's account
        /// and folder, runs the save, and puts the returned path (or the failure) into <see cref="SaveStatus"/>.</summary>
        async Task ReportSaveAsync(MailMessageSummary message, Func<MailAccountData, string, Task<string>> save, string successText = "Saved to")
        {
            if (MailUiState.SelectedAccount is not { } account || ResolveFolderOf(message) is not { } folderName) return;
            try
            {
                SaveStatus = $"💾 {successText} {await save(account, folderName)}";
            }
            catch (Exception ex)
            {
                SaveStatus = $"❌ {ex.Message}";
            }
        }

        async Task SaveAttachmentAsync(int attachmentIndex)
        {
            if (MailUiState.OpenMessage is { } open)
                await ReportSaveAsync(open, (account, folderName) => AttachmentService.SaveAsync(account, folderName, open, attachmentIndex));
        }

        async Task SaveAllAttachmentsAsync()
        {
            if (MailUiState.OpenMessage is { } open)
                await ReportSaveAsync(open, (account, folderName) => AttachmentService.SaveAllAsync(account, folderName, open), "All attachments saved to");
        }

        void StartReply(MailMessageSummary message, bool replyAll) =>
            WithAccountAndFolder(message, (account, folderName) =>
                MailUiState.OpenCompose(ComposeService.BuildReply(account, folderName, message, replyAll)));

        void StartForward(MailMessageSummary message) =>
            WithAccountAndFolder(message, (account, folderName) =>
                MailUiState.OpenCompose(ComposeService.BuildForward(account, folderName, message)));

        void ToggleOpenFlagged(MailMessageSummary message) =>
            WithAccountAndFolder(message, (account, folderName) => MessageActions.ToggleFlagged(account, folderName, message));

        void ToggleOpenImportant(MailMessageSummary message) =>
            WithAccountAndFolder(message, (account, folderName) => MessageActions.ToggleImportant(account, folderName, message));

        /// <summary>Raw MIME shown in the source modal is capped here; a 20 MB newsletter would otherwise freeze the render.</summary>
        const int MaxShownSourceChars = 256 * 1024;

        bool ShowMessageSource { get; set; }
        bool ShowRawSource { get; set; }
        List<(string Name, string Value)> SourceHeaders { get; set; } = [];
        string SourceRaw { get; set; } = string.Empty;

        /// <summary>Reads headers and raw MIME straight from the cached .eml — no protocol traffic, works offline.</summary>
        void OpenMessageSource(MailMessageSummary message)
        {
            if (MailUiState.SelectedAccount is not { } account || ResolveFolderOf(message) is not { } folderName) return;

            SourceHeaders = MessageStore.TryLoadMimeMessage(account.Id, folderName, message.Uid) is { } mime
                ? [.. mime.Headers.Select(h => (h.Field, h.Value))]
                : [];

            byte[]? rawBytes = MessageStore.TryLoadFullMessage(account.Id, folderName, message.Uid);
            string raw = rawBytes == null ? string.Empty : Encoding.UTF8.GetString(rawBytes);
            SourceRaw = raw.Length > MaxShownSourceChars
                ? raw[..MaxShownSourceChars] + $"\n\n… truncated at {MaxShownSourceChars / 1024} KB"
                : raw;

            ShowRawSource = false;
            ShowMessageSource = true;
        }

        Task CopySourceAsync() => ClipboardService.CopyAsync(ShowRawSource ? SourceRaw
            : string.Join('\n', SourceHeaders.Select(h => $"{h.Name}: {h.Value}")));

        /// <summary>Saves the open message to Downloads as .eml (raw MIME) or .html (rendered snapshot).</summary>
        Task ExportOpen(MailMessageSummary message, bool asHtml) =>
            ReportSaveAsync(message, (account, folderName) => Task.FromResult(asHtml
                ? MessageExportService.ExportHtml(account, folderName, message)
                : MessageExportService.ExportEml(account, folderName, message)));

        void DeleteOpen(MailMessageSummary message) =>
            WithAccountAndFolder(message, (account, folderName) =>
            {
                MessageActions.Delete(account, folderName, message);
                MailUiState.CloseMessage();
            });

        void OpenMessage(MailMessageSummary message)
        {
            // A click while composing looked dead: the compose pane owns the right side, so the
            // reader never appeared. Autosave the draft and close compose, then open the message.
            if (MailUiState.ActiveCompose != null)
                SaveAndCloseCompose();
            MailUiState.FocusMessage(message);
            MailUiState.OpenMessageInReader(message);
        }

        /// <summary>Rows in the local Drafts folder reopen in the compose pane instead of the reader.</summary>
        void OpenMessageOrDraft(MailMessageSummary message)
        {
            if (MailUiState.SelectedAccount is { } account
                && MailUiState.SelectedFolder is { IsLocal: true, Role: FolderRole.Drafts }
                && ComposeService.LoadDraft(account, message) is { } draft)
            {
                MailUiState.OpenCompose(draft);
                return;
            }
            OpenMessage(message);
        }
    }
}
