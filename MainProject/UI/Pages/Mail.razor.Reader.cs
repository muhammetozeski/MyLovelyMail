using System.Text;
using Microsoft.AspNetCore.Components.Web;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.UI.Components;
using MyLovelyMail.MainProject.UI.Elements.Modals;
using GlobalSettings = MyLovelyMail.MainProject.Stores.Settings;

namespace MyLovelyMail.MainProject.UI.Pages
{
    // Reader pane: open-message body, attachments, tags and single-message actions.
    public partial class Mail
    {
        internal string? OpenBodyHtml { get; set; }
        internal bool OpenBodyLoading { get; set; }
        internal bool OpenBodyBlockedImages { get; set; }
        internal string? BodyError { get; set; }
        MailMessageIdentity? loadedBodyKey;
        MailMessageIdentity? remoteImagesAllowedKey;
        int bodyLoadGeneration;

        /// <summary>Resolves a reader message through the current account and search-hit folder map.</summary>
        internal MailMessageIdentity? MessageIdentityOf(MailMessageSummary? message)
        {
            if (message == null || MailUiState.SelectedAccount is not { } account) return null;
            if (RowMessages.ContainsKey(message))
            {
                string? listedFolder = HitFolders.TryGetValue(message, out var hit) ? hit.FullName : MailUiState.SelectedFolder?.FullName;
                return listedFolder == null ? null : new(account.Id, listedFolder, message.Uid);
            }
            if (ReferenceEquals(MailUiState.OpenMessage, message) && MailUiState.OpenMessageKey is { } opened) return opened;
            if (ReferenceEquals(MailUiState.FocusedMessage, message) && MailUiState.FocusedMessageKey is { } focused) return focused;
            if (openThreadKey is { } threadKey && openThread?.Contains(message) == true)
                return new(threadKey.AccountId, threadKey.FolderFullName, message.Uid);
            return MailUiState.SelectedFolder is { } folder ? new(account.Id, folder.FullName, message.Uid) : null;
        }

        internal FolderRole ResolveFolderRole(MailMessageSummary message)
        {
            if (MailUiState.SelectedAccount is not { } account || ResolveFolderOf(message) is not { } folderName)
                return FolderRole.None;
            return MessageStore.GetFolders(account.Id).FirstOrDefault(f => f.FullName == folderName)?.Role ?? FolderRole.None;
        }

        internal List<AttachmentInfo> OpenAttachments { get; set; } = [];
        internal string? SaveStatus { get; set; }
        internal bool ShowTagPicker { get; set; }
        internal string NewTagName { get; set; } = string.Empty;

        internal UnsubscribeTargets? unsubscribeTargets;
        internal IReadOnlyList<AuthFinding> authFindings = [];
        IReadOnlyList<RuleAuditEntry> ruleTrace = [];

        public readonly record struct RuleTraceLine(string RuleName, string Action, string Argument, int Count);

        internal IReadOnlyList<RuleTraceLine> ruleTraceLines = [];
        internal bool ruleTraceExpanded;

        static readonly string[] ActionImportance =
        [
            nameof(FilterActionType.MoveToRemoteFolder),
            nameof(FilterActionType.MoveToLocalFolder),
            nameof(FilterActionType.AddTag),
            nameof(FilterActionType.MarkImportant),
            nameof(FilterActionType.Flag),
            nameof(FilterActionType.MarkRead),
            nameof(FilterActionType.Mute),
            nameof(FilterActionType.SetNotificationSound)
        ];

        static int ActionRank(string action)
        {
            int index = Array.IndexOf(ActionImportance, action);
            return index < 0 ? ActionImportance.Length : index;
        }

        internal static string RuleTraceLabel(RuleTraceLine line) => line.Action switch
        {
            nameof(FilterActionType.MoveToRemoteFolder) => $"📂 Moved to {line.Argument}",
            nameof(FilterActionType.MoveToLocalFolder) => $"💻 Moved to {line.Argument} on this PC",
            nameof(FilterActionType.AddTag) => $"🏷️ Tagged {line.Argument}",
            nameof(FilterActionType.MarkImportant) => "❗ Marked important",
            nameof(FilterActionType.Flag) => "⭐ Starred",
            nameof(FilterActionType.MarkRead) => "✉️ Marked read",
            nameof(FilterActionType.Mute) => "🔇 Muted",
            nameof(FilterActionType.SetNotificationSound) => $"🔔 Notification sound: {line.Argument}",
            "StopProcessing" => "✋ Stopped the later rules",
            _ => line.Action
        };

        static IReadOnlyList<RuleTraceLine> FoldTrace(IReadOnlyList<RuleAuditEntry> trace) =>
            [.. trace
                .GroupBy(static e => (e.RuleName, e.Action, e.Argument))
                .Select(static g => new RuleTraceLine(g.Key.RuleName, g.Key.Action, g.Key.Argument, g.Sum(static e => e.Count)))
                .OrderBy(static line => ActionRank(line.Action))
                .ThenBy(static line => line.RuleName, StringComparer.OrdinalIgnoreCase)];

        internal void OpenUnsubscribePage(string url)
        {
            if (MailUiState.SelectedAccount is not { TorOnly: false }) return;
            SaveStatus = ExternalLinkService.TryOpen(url, out string failureReason)
                ? "🌐 Unsubscribe page opened in your browser"
                : $"❌ {failureReason}";
            MouseActionService.ReportStatus(SaveStatus);
        }

        internal void ComposeUnsubscribeMail(UnsubscribeTargets targets)
        {
            if (MailUiState.SelectedAccount is not { } account || targets.MailtoAddress is not { } address) return;
            var draft = ComposeService.BuildNew(account);
            draft.To = address;
            draft.Subject = targets.MailtoSubject ?? "unsubscribe";
            MailUiState.OpenCompose(draft);
        }

        PersonProfile? senderHistory;
        MailMessageIdentity? senderHistoryKey;

        internal PersonProfile? HistoryOfOpenSender(MailMessageSummary open)
        {
            var key = MessageIdentityOf(open);
            if (key != null && senderHistoryKey == key) return senderHistory;
            senderHistoryKey = key;
            senderHistory = null;

            if (MailUiState.SelectedAccount is not { } account || open.FromAddress.Length == 0) return null;

            PersonProfile profile;
            using (PerfTrace.Measure("reader.senderHistory"))
                profile = PersonProfileService.Build(account.Id, open.FromAddress);
            senderHistory = profile.MessageCount > 1 ? profile : null;
            return senderHistory;
        }

        List<MailMessageSummary>? openThread;
        MailMessageIdentity? openThreadKey;

        internal List<MailMessageSummary>? ThreadOfOpenMessage(MailMessageSummary open)
        {
            var key = MessageIdentityOf(open);
            if (key != null && openThreadKey == key) return openThread;
            openThreadKey = key;
            openThread = null;

            if (MailUiState.SelectedAccount is not { } account || ResolveFolderOf(open) is not { } folderName)
                return null;

            MailThread? thread;
            using (PerfTrace.Measure("reader.thread"))
                thread = ThreadingService.BuildThreads(MessageStore.GetSummaries(account.Id, folderName))
                    .FirstOrDefault(t => t.Messages.Any(m => m.Uid == open.Uid));

            openThread = thread is { Messages.Count: > 1 } ? thread.Messages : null;
            return openThread;
        }

        internal int OpenThreadPosition(MailMessageSummary open) =>
            (ThreadOfOpenMessage(open)?.FindIndex(m => m.Uid == open.Uid) ?? -1) + 1;

        internal bool StepThread(int delta)
        {
            if (MailUiState.OpenMessage is not { } open || ThreadOfOpenMessage(open) is not { } thread) return false;
            int target = thread.FindIndex(m => m.Uid == open.Uid) + delta;
            if (target < 0 || target >= thread.Count) return false;
            OpenMessage(thread[target]);
            return true;
        }

        const int ReaderScaleStep = 10;

        internal void StepReaderTextScale(int delta)
        {
            var setting = GlobalSettings.ReaderTextScalePercent;
            setting.Value = delta == 0 ? 100 : Math.Clamp(setting.Value + delta * ReaderScaleStep, 70, 200);
            SettingsManager.SaveSettings();
            loadedBodyKey = null;
            _ = LoadOpenBodyIfNeededAsync();
        }

        internal void AllowImagesOnce()
        {
            if (MailUiState.OpenMessage is not { } open) return;
            remoteImagesAllowedKey = MessageIdentityOf(open);
            loadedBodyKey = null;
            _ = LoadOpenBodyIfNeededAsync();
        }

        async Task LoadOpenBodyIfNeededAsync()
        {
            var account = MailUiState.SelectedAccount;
            var open = MailUiState.OpenMessage;
            if (account == null || open == null)
            {
                OpenBodyHtml = null;
                OpenBodyLoading = false;
                loadedBodyKey = null;
                bodyLoadGeneration++;
                return;
            }
            if (ResolveFolderOf(open) is not { } folderName) return;
            var key = new MailMessageIdentity(account.Id, folderName, open.Uid);
            if (loadedBodyKey == key && (OpenBodyHtml != null || OpenBodyLoading)) return;

            int generation = ++bodyLoadGeneration;
            loadedBodyKey = key;
            OpenBodyHtml = null;
            BodyError = null;
            OpenBodyLoading = true;
            await InvokeAsync(StateHasChanged);

            bool allowRemote = key == remoteImagesAllowedKey;
            RenderedBody? rendered;
            using (PerfTrace.Measure("reader.render"))
                rendered = MailBodyRenderer.Render(account, folderName, open, allowRemote);
            if (rendered == null)
            {
                try
                {
                    if (account.Protocol == IncomingProtocol.Imap && !folderName.StartsWith(MessageStore.LocalFolderPrefix))
                        await ImapSyncService.DownloadMessageAsync(account, folderName, open.Uid);
                    else if (account.Protocol == IncomingProtocol.FastMail)
                        await Services.Mail.FastMail.FastMailSyncService.DownloadMessageAsync(account, open.Uid);
                    else if (account.Protocol == IncomingProtocol.Pop3)
                        await Pop3Service.DownloadMessageAsync(account, open.Uid);
                    rendered = MailBodyRenderer.Render(account, folderName, open, allowRemote);
                }
                catch (Exception ex)
                {
                    if (generation != bodyLoadGeneration || MessageIdentityOf(MailUiState.OpenMessage) != key) return;
                    BodyError = ex.Message;
                }
            }

            if (generation != bodyLoadGeneration || MessageIdentityOf(MailUiState.OpenMessage) != key) return;

            OpenBodyHtml = rendered?.Html;
            OpenBodyBlockedImages = rendered?.RemoteImagesBlocked ?? false;
            OpenBodyLoading = false;
            using (PerfTrace.Measure("reader.attachments"))
                OpenAttachments = open.HasAttachments ? AttachmentService.List(account, folderName, open) : [];
            using (PerfTrace.Measure("reader.unsubscribe"))
                unsubscribeTargets = UnsubscribeService.Read(account, folderName, open);
            using (PerfTrace.Measure("reader.auth"))
                authFindings = MessageAuthService.Read(account, folderName, open);
            using (PerfTrace.Measure("reader.ruleTrace"))
            {
                ruleTrace = RuleAuditStore.For(account.Id, folderName, open.Uid, open.MessageId);
                ruleTraceLines = FoldTrace(ruleTrace);
                ruleTraceExpanded = false;
            }
            using (PerfTrace.Measure("reader.markRead"))
                MarkOpenAsRead(account, folderName, open);
            await InvokeAsync(StateHasChanged);
        }

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
                if (ReferenceEquals(MailUiState.OpenMessage, open) && MailUiState.SelectedAccount?.Id == account.Id)
                    MessageActions.MarkRead(account, folderName, open);
            });
        }

        void WithAccountAndFolder(MailMessageSummary message, Action<MailAccountData, string> action)
        {
            if (MailUiState.SelectedAccount is { } account && ResolveFolderOf(message) is { } folderName)
                action(account, folderName);
        }

        internal void ToggleTagOnOpen(MailMessageSummary message, string tagName) =>
            WithAccountAndFolder(message, (account, folderName) => MessageActions.ToggleTag(account, folderName, message, tagName));

        internal void AddNewTag(MailMessageSummary message)
        {
            string tagName = NewTagName.Trim();
            if (tagName.Length == 0) return;
            TagStore.ColorOf(tagName);
            ToggleTagOnOpen(message, tagName);
            NewTagName = string.Empty;
        }

        async Task ReportSaveAsync(MailMessageSummary message, Func<MailAccountData, string, Task<string>> save, string successText = "Saved to")
        {
            if (MailUiState.SelectedAccount is not { } account || ResolveFolderOf(message) is not { } folderName) return;
            try
            {
                string savedPath = await save(account, folderName);
                SaveStatus = $"💾 {successText} {savedPath}";
                MouseActionService.ReportStatus(SaveStatus);
                FileSaveHelper.RevealInFileExplorer(savedPath);
            }
            catch (Exception ex)
            {
                SaveStatus = $"❌ {ex.Message}";
                MouseActionService.ReportStatus(SaveStatus);
            }
        }

        internal async Task SaveAttachmentAsync(int attachmentIndex)
        {
            if (MailUiState.OpenMessage is { } open)
                await ReportSaveAsync(open, (account, folderName) => AttachmentService.SaveAsync(account, folderName, open, attachmentIndex));
        }

        internal async Task SaveAllAttachmentsAsync()
        {
            if (MailUiState.OpenMessage is { } open)
                await ReportSaveAsync(open, (account, folderName) => AttachmentService.SaveAllAsync(account, folderName, open), "All attachments saved to");
        }

        internal void StartReply(MailMessageSummary message, bool replyAll) =>
            WithAccountAndFolder(message, (account, folderName) =>
                MailUiState.OpenCompose(ComposeService.BuildReply(account, folderName, message, replyAll)));

        internal void StartForward(MailMessageSummary message) =>
            WithAccountAndFolder(message, (account, folderName) =>
                MailUiState.OpenCompose(ComposeService.BuildForward(account, folderName, message)));

        internal void StartForwardAsAttachment(MailMessageSummary message) =>
            WithAccountAndFolder(message, (account, folderName) =>
                MailUiState.OpenCompose(ComposeService.BuildForwardAsAttachment(account, folderName, message)));

        internal void ToggleOpenFlagged(MailMessageSummary message) =>
            WithAccountAndFolder(message, (account, folderName) => MessageActions.ToggleFlagged(account, folderName, message));

        internal void ToggleOpenImportant(MailMessageSummary message) =>
            WithAccountAndFolder(message, (account, folderName) => MessageActions.ToggleImportant(account, folderName, message));

        internal IReadOnlyList<PinnedReaderAction> GetPinnedReaderActions(MailMessageSummary message) =>
            MailMenuItems.BuildPinnedReaderActions(this, message);

        internal void ToggleReaderPinnedAction(string pinId)
        {
            bool nowPinned = MailMenuItems.TogglePinAction(pinId);
            MouseActionService.ReportStatus(nowPinned ? $"📌 Pinned '{pinId}' to reader toolbar." : $"Unpinned '{pinId}' from reader toolbar.");
        }

        internal void OpenReplyMenu(MailMessageSummary message, MouseEventArgs pointer)
        {
            ContextMenuService.Open(this, "Reply & Forward", [
                new("↩️ Reply", () => StartReply(message, replyAll: false), pinActionId: "reply"),
                new("↩️ Reply all", () => StartReply(message, replyAll: true), pinActionId: "reply-all"),
                new("↪️ Forward", () => StartForward(message), pinActionId: "forward"),
                new("📎 Forward as attachment (.eml)", () => StartForwardAsAttachment(message), pinActionId: "forward-attachment")
            ], pointer.ClientX, pointer.ClientY);
        }

        internal void OpenMarkersModal(MailMessageSummary message)
        {
            var targets = MessageTargets(message);
            if (targets.Length == 0) return;
            ModalService.Open<MessageMarkersModal>(new Dictionary<string, object?>
            {
                [nameof(MessageMarkersModal.Targets)] = targets
            }, ModalSize.Compact);
        }

        internal void OpenBulkMarkersModal()
        {
            var targets = MailUiState.SelectedMessages.ToArray();
            if (targets.Length == 0) return;
            ModalService.Open<MessageMarkersModal>(new Dictionary<string, object?>
            {
                [nameof(MessageMarkersModal.Targets)] = targets
            }, ModalSize.Compact);
        }

        internal void OpenMoveModal(MailMessageSummary message)
        {
            var targets = MessageTargets(message);
            if (targets.Length == 0) return;
            ModalService.Open<MessageMoveModal>(new Dictionary<string, object?>
            {
                [nameof(MessageMoveModal.Targets)] = targets,
                [nameof(MessageMoveModal.Destinations)] = MoveTargets(targets)
            }, ModalSize.Compact);
        }

        internal void OpenBulkMoveModal()
        {
            var targets = MailUiState.SelectedMessages.ToArray();
            if (targets.Length == 0) return;
            ModalService.Open<MessageMoveModal>(new Dictionary<string, object?>
            {
                [nameof(MessageMoveModal.Targets)] = targets,
                [nameof(MessageMoveModal.Destinations)] = MoveTargets(targets)
            }, ModalSize.Compact);
        }

        internal void OpenSnoozeModal(MailMessageSummary message)
        {
            var targets = MessageTargets(message);
            if (targets.Length == 0) return;
            ModalService.Open<MessageSnoozeModal>(new Dictionary<string, object?>
            {
                [nameof(MessageSnoozeModal.Targets)] = targets
            }, ModalSize.Compact);
        }

        internal void OpenBulkSnoozeModal()
        {
            var targets = MailUiState.SelectedMessages.ToArray();
            if (targets.Length == 0) return;
            ModalService.Open<MessageSnoozeModal>(new Dictionary<string, object?>
            {
                [nameof(MessageSnoozeModal.Targets)] = targets
            }, ModalSize.Compact);
        }

        internal void OpenSaveModal(MailMessageSummary message, string initialFormat = "eml")
        {
            if (MailUiState.SelectedAccount is not { } account || ResolveFolderOf(message) is not { } folderName) return;
            ModalService.Open<MessageSaveModal>(new Dictionary<string, object?>
            {
                [nameof(MessageSaveModal.Account)] = account,
                [nameof(MessageSaveModal.FolderFullName)] = folderName,
                [nameof(MessageSaveModal.Message)] = message,
                [nameof(MessageSaveModal.InitialFormat)] = initialFormat
            }, ModalSize.Compact);
        }

        const int MaxShownSourceChars = 256 * 1024;

        /// <summary>Reads headers and raw MIME straight from the cached .eml and opens MessageSourceModal.</summary>
        internal void OpenMessageSource(MailMessageSummary message)
        {
            if (MailUiState.SelectedAccount is not { } account || ResolveFolderOf(message) is not { } folderName) return;

            var headers = MessageStore.TryLoadMimeMessage(account.Id, folderName, message.Uid) is { } mime
                ? mime.Headers.Select(h => (h.Field, h.Value)).ToList()
                : [];

            byte[]? rawBytes = MessageStore.TryLoadFullMessage(account.Id, folderName, message.Uid);
            string raw = rawBytes == null ? string.Empty : Encoding.UTF8.GetString(rawBytes);
            string sourceRaw = raw.Length > MaxShownSourceChars
                ? raw[..MaxShownSourceChars] + $"\n\n… truncated at {MaxShownSourceChars / 1024} KB"
                : raw;

            ModalService.Open<MessageSourceModal>(new Dictionary<string, object?>
            {
                [nameof(MessageSourceModal.Headers)] = headers,
                [nameof(MessageSourceModal.RawSource)] = sourceRaw
            }, ModalSize.Wide);
        }

        internal void OpenShortcutHelpModal() =>
            ModalService.Open<ShortcutHelpModal>(size: ModalSize.Dialog);

        /// <summary>Saves the open message to Downloads as .eml (raw MIME) or .html (rendered snapshot).</summary>
        internal Task ExportOpen(MailMessageSummary message, bool asHtml) =>
            ReportSaveAsync(message, (account, folderName) => Task.FromResult(asHtml
                ? MessageExportService.ExportHtml(account, folderName, message)
                : MessageExportService.ExportEml(account, folderName, message)));

        internal void OpenMessage(MailMessageSummary message)
        {
            if (MailUiState.ActiveCompose != null)
                SaveAndCloseCompose();
            var identity = MessageIdentityOf(message);
            MailUiState.FocusMessage(message, identity);
            MailUiState.OpenMessageInReader(message, identity);
        }

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
