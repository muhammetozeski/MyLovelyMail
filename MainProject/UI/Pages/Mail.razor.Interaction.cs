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
        bool ShowShortcutHelp { get; set; }

        static readonly (string Keys, string Action)[] ShortcutHelpRows =
        [
            ("J / ↓", "Focus next message"),
            ("K / ↑", "Focus previous message"),
            ("Enter", "Open focused message"),
            ("U", "Toggle read / unread"),
            ("S", "Toggle star (flag)"),
            ("I", "Toggle important"),
            ("M", "Mute / unmute the conversation"),
            ("X", "Tick / untick the focused row"),
            ("Ctrl+A", "Tick every listed row"),
            ("Ctrl+- / Ctrl+= / Ctrl+0", "Reader text smaller / larger / reset"),
            ("N", "Next in the open conversation"),
            ("P", "Previous in the open conversation"),
            ("Delete", "Delete message"),
            ("Escape", "Close reader / dialog"),
            ("Ctrl+1..9", "Switch account"),
            ("?", "Show this help"),
            ("Search", "from: to: tag: has:attachment is:unread is:starred is:important is:snoozed"),
            ("Search by date", "after:2026-08-01 before: on: newer_than:7d older_than:3m")
        ];

        /// <summary>Uids that arrived in the open folder since the previous refresh — they bloom once, then join the seen set.</summary>
        readonly HashSet<uint> bloomingUids = [];
        readonly HashSet<uint> seenUids = [];
        string seenUidsFolderKey = string.Empty;

        void HandleFolderChanged(string accountId, string folderFullName)
        {
            if (MailUiState.SelectedAccount?.Id != accountId) return;
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
            if (e.CtrlKey)
                MailUiState.ToggleSelected(message.Uid);
            else
                OpenMessageOrDraft(message);
        }

        /// <summary>Checked rows' messages. In conversation mode a checked row means its newest message, matching what the row shows.</summary>
        List<MailMessageSummary> SelectedSummaries =>
            [.. Rows.Select(static r => r.Message).Where(s => MailUiState.SelectedUids.Contains(s.Uid))];

        /// <summary>Runs the action on every selected row (each with its real folder resolved), then clears the selection.</summary>
        void ForEachSelected(Action<MailAccountData, string, MailMessageSummary> action)
        {
            if (MailUiState.SelectedAccount is not { } account) return;
            foreach (var summary in SelectedSummaries)
                if (ResolveFolderOf(summary) is { } folderName)
                    action(account, folderName, summary);
            MailUiState.ClearSelection();
        }

        /// <summary>
        /// Marking read goes through the batched path — one index rewrite and one IMAP connection
        /// for the whole selection instead of one of each per row. Unmarking stays per-message:
        /// it is a correction on a handful of rows, never a folder-sized action.
        /// </summary>
        void BulkSetRead(bool read)
        {
            if (MailUiState.SelectedAccount is not { } account) return;
            if (!read)
            {
                ForEachSelected((a, folderName, summary) => MessageActions.SetRead(a, folderName, summary, read: false));
                return;
            }

            foreach (var perFolder in SelectedSummaries.GroupBy(ResolveFolderOf).Where(static g => g.Key != null))
                MessageActions.SetManyRead(account, perFolder.Key!, perFolder);
            MailUiState.ClearSelection();
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
        void RetryHeldOutbox()
        {
            OutboxAttemptStore.ReleaseHolds();
            _ = OutboxService.FlushAsync();
        }

        /// <summary>Result of the last folder-level action, shown under the folder list.</summary>
        string? FolderActionStatus { get; set; }

        /// <summary>
        /// Writes the folder as one mbox file. The count of bodies that were never downloaded is
        /// reported rather than swallowed — an export that claims to be complete and is not would
        /// be found out only by whoever imports it.
        /// </summary>
        void ExportFolderMailbox(MailFolderData folder)
        {
            if (AccountStore.GetById(folder.AccountId) is not { } account) return;
            try
            {
                var result = MailboxExportService.ExportFolder(account, folder.FullName);
                FolderActionStatus = result.SkippedUncached > 0
                    ? $"📦 {result.Written} of {result.Written + result.SkippedUncached} saved to {result.Path} — {result.SkippedUncached} bodies were never downloaded"
                    : $"📦 {result.Written} saved to {result.Path}";
            }
            catch (Exception ex)
            {
                FolderActionStatus = $"❌ {ex.Message}";
            }
        }

        /// <summary>Clears a whole folder's unread count from the sidebar, without checking rows by hand.</summary>
        void MarkFolderRead(MailFolderData folder)
        {
            if (AccountStore.GetById(folder.AccountId) is not { } account) return;
            MessageActions.SetFolderRead(account, folder.FullName);
        }

        void BulkToggleFlag() => ForEachSelected(MessageActions.ToggleFlagged);

        void BulkToggleImportant() => ForEachSelected(MessageActions.ToggleImportant);

        void BulkDelete() => ForEachSelected(MessageActions.Delete);

        /// <summary>Hearts drifting up behind the Inbox Zero headline.</summary>
        const int InboxZeroHeartCount = 5;

        /// <summary>Spreads the hearts across the width and staggers their loop so they never drift in lockstep.
        /// Invariant formatting is mandatory: a Turkish decimal comma would make the CSS delay invalid.</summary>
        static string InboxZeroHeartStyle(int heartIndex) => string.Create(CultureInfo.InvariantCulture,
            $"left:{10 + heartIndex * 18}%;animation-delay:{heartIndex * 0.7:0.0}s;font-size:{14 + heartIndex % 3 * 6}px;");

        bool ShowSnoozePicker { get; set; }

        /// <summary>Hides every checked row until the chosen moment; they come back unread.</summary>
        void BulkSnooze(Func<DateTime> dueUtc)
        {
            ShowSnoozePicker = false;
            if (MailUiState.SelectedAccount is not { } account) return;
            var due = dueUtc();
            foreach (var summary in SelectedSummaries)
                if (ResolveFolderOf(summary) is { } folderName)
                    SnoozeService.Snooze(account, folderName, summary, due);
            MailUiState.ClearSelection();
        }

        /// <summary>Pulls a snoozed message back into the list right away.</summary>
        void WakeMessage(MailMessageSummary summary)
        {
            if (MailUiState.SelectedAccount is { } account && ResolveFolderOf(summary) is { } folderName)
                SnoozeService.Wake(account, folderName, summary);
        }

        /// <summary>Row chip text: when this message is due back.</summary>
        static string SnoozeLabel(DateTime dueUtc)
        {
            var due = dueUtc.ToLocalTime();
            return due.Date == DateTime.Today ? $"💤 {due:HH:mm}" : $"💤 {due:dd MMM HH:mm}";
        }

        bool ShowMovePicker { get; set; }

        /// <summary>
        /// Where the selection may go: from an IMAP server folder anywhere else (server or local),
        /// from a local folder or a POP3 account only into other local folders — nothing can be
        /// pushed UP to a server, since no upload path exists.
        /// </summary>
        List<MailFolderData> MoveTargets
        {
            get
            {
                if (MailUiState.SelectedAccount is not { } account || MailUiState.SelectedFolder is not { } current)
                    return [];

                bool serverSource = account.Protocol == IncomingProtocol.Imap && !current.IsLocal;
                return [.. Folders.Select(static n => n.Folder).Where(f => f.FullName != current.FullName && (serverSource || f.IsLocal))];
            }
        }

        void BulkMoveTo(MailFolderData targetFolder)
        {
            ShowMovePicker = false;
            if (MailUiState.SelectedAccount is not { } account || MailUiState.SelectedFolder is not { } current) return;

            if (targetFolder.IsLocal)
                MessageActions.MoveToLocalFolder(account, current.FullName, SelectedSummaries, targetFolder.DisplayName);
            else
                MessageActions.MoveToFolder(account, current.FullName, SelectedSummaries, targetFolder.FullName);
            MailUiState.ClearSelection();
        }

        void HandleListKeyDown(KeyboardEventArgs e)
        {
            var account = MailUiState.SelectedAccount;
            if (account == null) return;

            var focused = MailUiState.FocusedMessage;
            string? focusedFolder = focused == null ? null : ResolveFolderOf(focused);
            switch (e.Key)
            {
                case "j" or "J" or "ArrowDown":
                    MoveFocus(1);
                    break;
                case "k" or "K" or "ArrowUp":
                    MoveFocus(-1);
                    break;
                case "Enter" when focused != null:
                    MailUiState.OpenMessageInReader(focused);
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
                // The row checkbox is a span with @onclick, so ticking one row had no keyboard
                // route at all — only Ctrl+A for the whole list.
                case "m" or "M" when focusedFolder != null:
                    MessageActions.ToggleMuted(account, focusedFolder, focused!);
                    break;
                case "x" or "X" when focused != null:
                    MailUiState.ToggleSelected(focused.Uid);
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
                    if (ShowShortcutHelp) ShowShortcutHelp = false;
                    else if (MailUiState.SelectedUids.Count > 0) MailUiState.ClearSelection();
                    else MailUiState.CloseMessage();
                    break;
                // Ctrl with the zoom keys, the shape every reader uses.
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
                    MailUiState.SelectMany(Rows.Select(static r => r.Message.Uid));
                    break;
                case "?":
                    ShowShortcutHelp = !ShowShortcutHelp;
                    break;
                case var digit when e.CtrlKey && digit.Length == 1 && digit[0] is >= '1' and <= '9':
                    int accountIndex = digit[0] - '1';
                    if (accountIndex < AccountStore.Accounts.Count)
                        SelectAccount(AccountStore.Accounts[accountIndex]);
                    break;
            }
        }

        /// <summary>Walks the rendered rows, so j/k steps conversation by conversation when grouping is on.</summary>
        void MoveFocus(int delta)
        {
            if (Rows.Count == 0) return;
            var focusable = Rows.Select(static r => r.Message).ToList();
            int index = MailUiState.FocusedMessage == null ? -1 : focusable.IndexOf(MailUiState.FocusedMessage);
            int next = Math.Clamp(index + delta, 0, focusable.Count - 1);
            MailUiState.FocusMessage(focusable[next]);
        }

        /// <summary>1-2 initials for the avatar circle: from the display name's words, else the address.</summary>
        static string AvatarInitials(MailMessageSummary message)
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
        static string AvatarGradient(MailMessageSummary message)
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
