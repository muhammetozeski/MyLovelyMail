using System.Globalization;
using Microsoft.AspNetCore.Components.Web;
using MyLovelyMail.MainProject.Constants.ThemeConstants;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.UI.Pages
{
    // List interaction: keyboard shortcuts, bulk selection actions, new-arrival bloom
    // tracking and the small per-row visual helpers.
    public partial class Mail
    {
        /// <summary>Uids that arrived in the open folder since the previous refresh — they bloom once, then join the seen set.</summary>
        readonly HashSet<uint> bloomingUids = [];
        readonly HashSet<uint> seenUids = [];
        string seenUidsFolderKey = string.Empty;

        void HandleFolderChanged(string accountId, string folderFullName)
        {
            if (MailUiState.SelectedAccount?.Id != accountId) return;
            senderHistoryKey = null;
            if (MailUiState.OpenMessageKey?.FolderFullName == folderFullName) openThreadKey = null;
            RefreshLists();
            TrackNewArrivalsForBloom(accountId, folderFullName);
            InvokeAsync(StateHasChanged);
        }

        void TrackNewArrivalsForBloom(string accountId, string folderFullName)
        {
            if (MailUiState.SelectedFolder?.FullName != folderFullName) return;

            string folderKey = MessageStore.FolderKey(accountId, folderFullName);
            if (seenUidsFolderKey != folderKey)
            {
                // Folder switch: everything currently listed counts as seen, nothing blooms.
                seenUidsFolderKey = folderKey;
                seenUids.Clear();
                bloomingUids.Clear();
                foreach (var summary in FilteredSummaries) seenUids.Add(summary.Uid);
                return;
            }

            foreach (var summary in FilteredSummaries)
                if (seenUids.Add(summary.Uid) && summary.IsUnread)
                    bloomingUids.Add(summary.Uid);
        }

        /// <summary>Ctrl+Click toggles selection for bulk actions; a plain click opens the message (or its draft).</summary>
        void HandleRowClick(MouseEventArgs e, MailMessageSummary message)
        {
            if (e.CtrlKey || e.ShiftKey || SelectionMode)
                ToggleRowSelection(e, message);
            else
            {
                SelectionAnchor = MessageIdentityOf(message);
                OpenMessageOrDraft(message);
            }
        }

        /// <summary>How a queued message is doing, or null when this is not an Outbox row.</summary>
        string? OutboxStatusOf(MailMessageSummary message)
        {
            if (MailUiState.SelectedFolder is not { Role: FolderRole.Outbox } outbox) return null;
            var attempt = OutboxAttemptStore.For(outbox.AccountId, message.Uid);
            if (attempt is null || attempt.AttemptCount == 0) return "⏳ Queued";
            return attempt.HeldUtc != null
                ? $"⛔ Stopped after {attempt.AttemptCount} tries — {attempt.LastError}"
                : $"⏳ Queued, {attempt.AttemptCount} tries — {attempt.LastError}";
        }

        /// <summary>Releases every hold and flushes now; the bar that offers this only appears when something is held.</summary>
        internal void RetryHeldOutbox()
        {
            OutboxAttemptStore.ReleaseHolds();
            _ = OutboxService.FlushAsync();
        }

        /// <summary>
        /// Writes the folder as one mbox file. The count of bodies that were never downloaded is
        /// reported rather than swallowed — an export that claims to be complete and is not would
        /// be found out only by whoever imports it.
        /// </summary>
        internal void ExportFolderMailbox(MailFolderData folder)
        {
            if (AccountStore.GetById(folder.AccountId) is not { } account) return;
            try
            {
                var result = MailboxExportService.ExportFolder(account, folder.FullName);
                string status = result.SkippedUncached > 0
                    ? $"📦 {result.Written} of {result.Written + result.SkippedUncached} saved to {result.Path} — {result.SkippedUncached} bodies were never downloaded"
                    : $"📦 {result.Written} saved to {result.Path}";
                MouseActionService.ReportStatus(status);
                FileSaveHelper.RevealInFileExplorer(result.Path);
            }
            catch (Exception ex)
            {
                Log($"Folder export failed: account={folder.AccountId}; folder='{folder.FullName}'; {ex}", LogLevel.Error);
                MouseActionService.ReportStatus($"❌ {ex.Message}");
            }
        }

        /// <summary>Hearts drifting up behind the Inbox Zero headline.</summary>
        const int InboxZeroHeartCount = 5;

        /// <summary>Spreads the hearts across the width and staggers their loop so they never drift in lockstep.
        /// Invariant formatting is mandatory: a Turkish decimal comma would make the CSS delay invalid.</summary>
        static string InboxZeroHeartStyle(int heartIndex) => string.Create(CultureInfo.InvariantCulture,
            $"left:{10 + heartIndex * 18}%;animation-delay:{heartIndex * 0.7:0.0}s;font-size:{14 + heartIndex % 3 * 6}px;");

        /// <summary>The occupants that actually have state behind them, in the order they are offered.</summary>
        internal List<(MailUiState.PaneOccupant Occupant, string Label)> PaneTabs
        {
            get
            {
                List<(MailUiState.PaneOccupant, string)> tabs = [];
                if (MailUiState.OpenMessage != null) tabs.Add((MailUiState.PaneOccupant.Reader, "📖 Message"));
                if (MailUiState.ActiveCompose != null) tabs.Add((MailUiState.PaneOccupant.Compose, "✏️ Draft"));
                if (MailUiState.OpenPersonAddress != null) tabs.Add((MailUiState.PaneOccupant.Person, "👤 Person"));
                return tabs;
            }
        }

        /// <summary>Pulls a snoozed message back into the list right away.</summary>
        Task WakeMessage(MailMessageSummary summary) =>
            MouseActionService.RunMessagesAsync(MailMessageCommand.Wake, MessageTargets(summary));

        /// <summary>Row chip text: when this message is due back.</summary>
        static string SnoozeLabel(DateTime dueUtc)
        {
            var due = dueUtc.ToLocalTime();
            return due.Date == DateTime.Today ? $"💤 {due:HH:mm}" : $"💤 {due:dd MMM HH:mm}";
        }

        void HandleListKeyDown(KeyboardEventArgs e)
        {
            if (e.Key == "?")
            {
                OpenShortcutHelpModal();
                return;
            }
            if (e.Key == "Escape" && ModalService.Active != null)
            {
                ModalService.Close();
                return;
            }
            var account = MailUiState.SelectedAccount;
            if (account == null) return;
            // Modified browser shortcuts must not accidentally trigger plain-letter triage.
            if (e.AltKey || e.MetaKey) return;
            if (e.CtrlKey && e.ShiftKey && e.Key is "a" or "A")
            {
                InvertListedSelection();
                return;
            }
            if (e.CtrlKey && e.Key is not ("a" or "A" or "+" or "=" or "-" or "_" or "0")
                && !(e.Key.Length == 1 && e.Key[0] is >= '1' and <= '9')) return;

            var focused = MailUiState.FocusedMessage;
            string? focusedFolder = focused == null ? null : ResolveFolderOf(focused);
            switch (e.Key)
            {
                case "ArrowDown" when e.ShiftKey:
                    ExtendFocusedSelection(1);
                    break;
                case "ArrowUp" when e.ShiftKey:
                    ExtendFocusedSelection(-1);
                    break;
                case "Home":
                    FocusListBoundary(false);
                    break;
                case "End":
                    FocusListBoundary(true);
                    break;
                case "j" or "J" or "ArrowDown":
                    MoveFocus(1);
                    break;
                case "k" or "K" or "ArrowUp":
                    MoveFocus(-1);
                    break;
                case "Enter" when focused != null:
                    OpenMessage(focused);
                    break;
                case "u" or "U" when focusedFolder != null:
                    MessageActions.ToggleRead(account, focusedFolder, focused!);
                    break;
                case "s" or "S" when focusedFolder != null:
                    MessageActions.ToggleFlagged(account, focusedFolder, focused!);
                    break;
                case "i" or "I" when focusedFolder != null:
                    MessageActions.ToggleImportant(account, focusedFolder, focused!);
                    break;
                case "m" or "M" when focusedFolder != null:
                    MessageActions.ToggleMuted(account, focusedFolder, focused!);
                    break;
                case "t" or "T":
                    if (MailUiState.SelectedMessages.Count > 0) OpenBulkMarkersModal();
                    else if (focused != null) OpenMarkersModal(focused);
                    break;
                case "x" or "X" when focused != null:
                    ToggleRowSelection(new MouseEventArgs(), focused);
                    break;
                case "n" or "N" when MailUiState.OpenMessage != null:
                    StepThread(1);
                    break;
                case "p" or "P" when MailUiState.OpenMessage != null:
                    StepThread(-1);
                    break;
                case "Delete" when focusedFolder != null:
                    MessageActions.Delete(account, focusedFolder, focused!);
                    MailUiState.FocusMessage(null);
                    break;
                case "Escape":
                    if (ModalService.Active != null) ModalService.Close();
                    else if (MailUiState.SelectedMessages.Count > 0) MailUiState.ClearSelection();
                    else MailUiState.CloseMessage();
                    break;
                case "+" or "=" when e.CtrlKey:
                    StepReaderTextScale(1);
                    break;
                case "-" or "_" when e.CtrlKey:
                    StepReaderTextScale(-1);
                    break;
                case "0" when e.CtrlKey:
                    StepReaderTextScale(0);
                    break;
                case "a" or "A" when e.CtrlKey:
                    SelectAllMessages();
                    break;
                case "?":
                    OpenShortcutHelpModal();
                    break;
                case var digit when e.CtrlKey && digit.Length == 1 && digit[0] is >= '1' and <= '9':
                    int accountIndex = digit[0] - '1';
                    if (accountIndex < AccountStore.Accounts.Count)
                        MailUiState.SelectAccount(AccountStore.Accounts[accountIndex], restoreFolder: true);
                    break;
            }
        }

        /// <summary>Walks the rendered rows, so j/k steps conversation by conversation when grouping is on.</summary>
        void MoveFocus(int delta)
        {
            if (Rows.Count == 0) return;
            var focusable = Rows.Select(static r => r.Message).ToList();
            int index = FocusedRowIndex();
            int next = Math.Clamp(index + delta, 0, focusable.Count - 1);
            MailUiState.FocusMessage(focusable[next], MessageIdentityOf(focusable[next]));
        }

        /// <summary>1-2 initials for the avatar circle: from the display name's words, else the address.</summary>
        internal static string AvatarInitials(MailMessageSummary message)
        {
            string source = string.IsNullOrWhiteSpace(message.FromName) ? message.FromAddress : message.FromName;
            var words = source.Split([' ', '.', '@', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
            return words.Length switch
            {
                0 => "?",
                1 => char.ToUpperInvariant(words[0][0]).ToString(),
                _ => $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}"
            };
        }

        /// <summary>Deterministic pastel gradient per sender: stable address hash into the theme's MonthGradients.</summary>
        internal static string AvatarGradient(MailMessageSummary message)
        {
            var gradients = AppColors.MonthGradients;
            var (start, end) = gradients[(int)(Pop3Service.Fnv1aHash(message.FromAddress.ToLowerInvariant()) % (uint)gradients.Length)];
            return $"linear-gradient(135deg, {start.ToRgbaHex(true)}, {end.ToRgbaHex(true)})";
        }

        /// <summary>Today → clock; this year → day+month; older → full date.</summary>
        static string FormatDate(DateTime dateUtc)
        {
            var local = dateUtc.ToLocalTime();
            var now = DateTime.Now;
            if (local.Date == now.Date) return local.ToString("HH:mm");
            if (local.Year == now.Year) return local.ToString("dd MMM");
            return local.ToString("dd MMM yyyy");
        }
    }
}
