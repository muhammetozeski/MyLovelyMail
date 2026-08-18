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
            ("Delete", "Delete message"),
            ("Escape", "Close reader / dialog"),
            ("Ctrl+1..9", "Switch account"),
            ("?", "Show this help"),
            ("Search", "from: to: tag: has:attachment is:unread is:starred")
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

        List<MailMessageSummary> SelectedSummaries =>
            [.. FilteredSummaries.Where(s => MailUiState.SelectedUids.Contains(s.Uid))];

        /// <summary>Runs the action on every selected row (each with its real folder resolved), then clears the selection.</summary>
        void ForEachSelected(Action<MailAccountData, string, MailMessageSummary> action)
        {
            if (MailUiState.SelectedAccount is not { } account) return;
            foreach (var summary in SelectedSummaries)
                if (ResolveFolderOf(summary) is { } folderName)
                    action(account, folderName, summary);
            MailUiState.ClearSelection();
        }

        void BulkSetRead(bool read) =>
            ForEachSelected((account, folderName, summary) => MessageActions.SetRead(account, folderName, summary, read));

        void BulkToggleFlag() => ForEachSelected(MessageActions.ToggleFlagged);

        void BulkToggleImportant() => ForEachSelected(MessageActions.ToggleImportant);

        void BulkDelete() => ForEachSelected(MessageActions.Delete);

        bool ShowMovePicker { get; set; }

        /// <summary>Server folders the selection can move to: everything except the open folder and app-local ones. Empty for POP3 accounts, which hides the Move button entirely.</summary>
        List<MailFolderData> MoveTargets =>
            MailUiState.SelectedAccount is { Protocol: IncomingProtocol.Imap }
            && MailUiState.SelectedFolder is { IsLocal: false } current
                ? [.. Folders.Where(f => !f.IsLocal && f.FullName != current.FullName)]
                : [];

        void BulkMoveTo(MailFolderData targetFolder)
        {
            ShowMovePicker = false;
            if (MailUiState.SelectedAccount is not { } account || MailUiState.SelectedFolder is not { } current) return;
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
                case "Delete" when focusedFolder != null:
                    MessageActions.Delete(account, focusedFolder, focused!);
                    MailUiState.FocusMessage(null);
                    break;
                case "Escape":
                    if (ShowShortcutHelp) ShowShortcutHelp = false;
                    else if (MailUiState.SelectedUids.Count > 0) MailUiState.ClearSelection();
                    else MailUiState.CloseMessage();
                    break;
                case "a" or "A" when e.CtrlKey:
                    MailUiState.SelectMany(FilteredSummaries.Select(s => s.Uid));
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

        void MoveFocus(int delta)
        {
            if (FilteredSummaries.Count == 0) return;
            int index = MailUiState.FocusedMessage == null ? -1 : FilteredSummaries.IndexOf(MailUiState.FocusedMessage);
            int next = Math.Clamp(index + delta, 0, FilteredSummaries.Count - 1);
            MailUiState.FocusMessage(FilteredSummaries[next]);
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
