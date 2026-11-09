using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.UI.Components;
using GlobalSettings = MyLovelyMail.MainProject.Stores.Settings;

namespace MyLovelyMail.MainProject.UI.Pages
{
    /// <summary>One rendered list row. <paramref name="ConversationCount"/> is 1 in flat mode, so the
    /// same markup serves both modes; the count pill appears only above 1.</summary>
    public sealed record MailRow(MailMessageSummary Message, int ConversationCount, int UnreadCount, string SenderText)
    {
        /// <summary>Actual members behind a collapsed row; selection and triage affect the complete conversation.</summary>
        public IReadOnlyList<MailMessageSummary> Messages { get; init; } = [Message];
    }

    // Core of the mail screen: lifecycle/event wiring plus the folder list, message list and
    // search. The reader, compose and interaction members live in the sibling partials.
    public partial class Mail
    {
        [Inject] IJSRuntime JS { get; set; } = default!;

        string _searchText = string.Empty;

        internal string SearchText
        {
            get => _searchText;
            set
            {
                _searchText = value;
                RefreshLists();
            }
        }

        internal string? FolderActionStatus { get; set; }

        /// <summary>The folder rows in display order, each carrying its depth in the server's tree.</summary>
        internal List<FolderNode> Folders { get; set; } = [];

        /// <summary>The open folder's summaries after the search box filter, newest first.</summary>
        internal List<MailMessageSummary> FilteredSummaries { get; set; } = [];

        internal bool SearchAllFolders { get; set; }

        /// <summary>Search-hit summary -> (folder full name, display name), filled only in all-folders mode.</summary>
        Dictionary<MailMessageSummary, (string FullName, string Display)> HitFolders { get; set; } = [];

        /// <summary>Messages currently being dragged from the middle column onto a sidebar folder.</summary>
        internal MailMessageIdentity[] DraggedMessages { get; private set; } = [];

        internal void StartMessageDrag(MailMessageIdentity[] rowTargets)
        {
            DraggedMessages = rowTargets.Any(MailUiState.SelectedMessages.Contains)
                ? [.. MailUiState.SelectedMessages]
                : rowTargets;
        }

        internal void EndMessageDrag() => DraggedMessages = [];

        internal void ToggleMailFolderExpanded(MailFolderData folder)
        {
            MessageStore.ToggleFolderExpanded(folder.AccountId, folder.FullName);
            RefreshSidebarFolders();
        }

        internal void RefreshSidebarFolders()
        {
            var account = MailUiState.SelectedAccount;
            Folders = account == null ? [] : FolderTreeService.Build(MessageStore.GetFolders(account.Id));
            StateHasChanged();
        }

        internal int activeSplitter;
        double dragStartClientX;
        double dragStartSidebarPct;
        double dragStartListPct;
        double dragUsableWidth = 1200.0;

        internal void StartColumnSplitterDrag(int splitterIndex, PointerEventArgs e)
        {
            if (e.Button != 0) return;
            activeSplitter = splitterIndex;
            dragStartClientX = e.ClientX;
            dragStartSidebarPct = GlobalSettings.SidebarColumnPercent.Value;
            dragStartListPct = GlobalSettings.ListColumnPercent.Value;

            if (splitterIndex == 1)
            {
                double col1Px = Math.Max(180.0, e.ClientX - 24.0);
                dragUsableWidth = Math.Clamp(col1Px / (Math.Max(12.0, dragStartSidebarPct) / 100.0), 600.0, 3840.0);
            }
            else
            {
                double col1And2Px = Math.Max(440.0, e.ClientX - 40.0);
                dragUsableWidth = Math.Clamp(col1And2Px / (Math.Max(30.0, dragStartSidebarPct + dragStartListPct) / 100.0), 600.0, 3840.0);
            }
            StateHasChanged();
        }

        internal void OnScreenPointerMove(PointerEventArgs e)
        {
            if (activeSplitter == 0) return;
            if ((e.Buttons & 1) == 0)
            {
                FinishColumnSplitterDrag();
                return;
            }

            double deltaPct = ((e.ClientX - dragStartClientX) / dragUsableWidth) * 100.0;
            double sb = dragStartSidebarPct;
            double ls = dragStartListPct;

            if (activeSplitter == 1)
            {
                double totalFirstTwo = dragStartSidebarPct + dragStartListPct;
                sb = Math.Clamp(dragStartSidebarPct + deltaPct, 12.0, Math.Min(38.0, totalFirstTwo - 16.0));
                if (Math.Abs(sb - GlobalSettings.DefaultSidebarColumnPercent) <= 1.2 && (totalFirstTwo - GlobalSettings.DefaultSidebarColumnPercent) >= 16.0)
                    sb = GlobalSettings.DefaultSidebarColumnPercent;
                ls = Math.Clamp(totalFirstTwo - sb, 16.0, 58.0);
            }
            else if (activeSplitter == 2)
            {
                double maxList = Math.Max(16.0, 100.0 - sb - 22.0);
                ls = Math.Clamp(dragStartListPct + deltaPct, 16.0, Math.Min(58.0, maxList));
                if (Math.Abs(ls - GlobalSettings.DefaultListColumnPercent) <= 1.2 && ls <= maxList)
                    ls = GlobalSettings.DefaultListColumnPercent;
                else if (Math.Abs((100.0 - sb - ls) - GlobalSettings.DefaultReaderColumnPercent) <= 1.2 && (100.0 - sb - GlobalSettings.DefaultReaderColumnPercent) >= 16.0)
                    ls = 100.0 - sb - GlobalSettings.DefaultReaderColumnPercent;
            }

            ApplyColumnSplit(sb, ls, saveToDisk: false);
        }

        internal void OnScreenPointerUp(PointerEventArgs e)
        {
            if (activeSplitter == 0) return;
            FinishColumnSplitterDrag();
        }

        internal void OnScreenKeyDown(KeyboardEventArgs e)
        {
            if (activeSplitter != 0 && e.Key == "Escape")
            {
                activeSplitter = 0;
                ApplyColumnSplit(dragStartSidebarPct, dragStartListPct, saveToDisk: true);
                MouseActionService.ReportStatus("Column resize cancelled.");
                return;
            }
            if (e.Key == "Escape" && ModalService.Active != null)
            {
                ModalService.Close();
            }
        }

        void FinishColumnSplitterDrag()
        {
            if (activeSplitter == 0) return;
            activeSplitter = 0;
            ApplyColumnSplit(GlobalSettings.SidebarColumnPercent.Value, GlobalSettings.ListColumnPercent.Value, saveToDisk: true);
        }

        internal void ResetColumnSplitter(int splitterIndex)
        {
            activeSplitter = 0;
            if (splitterIndex == 1)
            {
                GlobalSettings.SidebarColumnPercent.Set(GlobalSettings.DefaultSidebarColumnPercent);
                MouseActionService.ReportStatus("Sidebar width reset to 20.0%.");
            }
            else
            {
                GlobalSettings.ListColumnPercent.Set(GlobalSettings.DefaultListColumnPercent);
                GlobalSettings.ReaderColumnPercent.Set(GlobalSettings.DefaultReaderColumnPercent);
                MouseActionService.ReportStatus("Message list width reset to 32.0%.");
            }
            SettingsManager.SaveSettings();
            Events.MainEvents.Trigger("OnStyleChanged");
        }

        void ApplyColumnSplit(double sidebarPct, double listPct, bool saveToDisk)
        {
            double sb = Math.Round(sidebarPct, 2);
            double ls = Math.Round(listPct, 2);
            double rd = Math.Round(Math.Max(22.0, 100.0 - sb - ls), 2);
            GlobalSettings.SidebarColumnPercent.Set(sb);
            GlobalSettings.ListColumnPercent.Set(ls);
            GlobalSettings.ReaderColumnPercent.Set(rd);
            if (saveToDisk)
                SettingsManager.SaveSettings();
            Events.MainEvents.Trigger("OnStyleChanged");
            StateHasChanged();
        }

        protected override void OnInitialized()
        {
            base.OnInitialized();
            MailUiState.OnSelectionChanged += HandleStateChanged;
            AccountStore.OnAccountsChanged += HandleStateChanged;
            AccountFolderStore.OnFoldersChanged += HandleStateChanged;
            MessageStore.OnFolderChanged += HandleFolderChanged;
            SyncScheduler.OnSyncStateChanged += HandleSyncStateChanged;
            ImapSyncService.OnFolderSyncStateChanged += HandleSyncStateChanged;
            OutboxService.OnChanged += HandleOutboxChanged;
            MouseActionService.MailMenuRequested = HandleNativeMailMenuAsync;
            MessageActions.OnMoveStateChanged += HandleMoveStateChanged;
            ModalService.OnChanged += HandleModalServiceChanged;
            MailMenuItems.OnPinnedActionsChanged += HandleSyncStateChanged;

            if (!TrySelectFirstAccount())
                RefreshLists();
        }

        public void Dispose()
        {
            draftAutosaveTimer?.Dispose();
            undoCountdownTimer?.Dispose();
            MailUiState.OnSelectionChanged -= HandleStateChanged;
            AccountStore.OnAccountsChanged -= HandleStateChanged;
            AccountFolderStore.OnFoldersChanged -= HandleStateChanged;
            MessageStore.OnFolderChanged -= HandleFolderChanged;
            SyncScheduler.OnSyncStateChanged -= HandleSyncStateChanged;
            ImapSyncService.OnFolderSyncStateChanged -= HandleSyncStateChanged;
            OutboxService.OnChanged -= HandleOutboxChanged;
            MouseActionService.MailMenuRequested = null;
            MessageActions.OnMoveStateChanged -= HandleMoveStateChanged;
            ModalService.OnChanged -= HandleModalServiceChanged;
            MailMenuItems.OnPinnedActionsChanged -= HandleSyncStateChanged;
            ContextMenuService.Close();
        }

        void HandleModalServiceChanged() => InvokeAsync(StateHasChanged);

        bool TrySelectFirstAccount()
        {
            if (MailUiState.SelectedAccount != null || AccountStore.Accounts.Count == 0) return false;
            var lastUsed = FolderMemoryStore.LastAccountId is { } accountId ? AccountStore.GetById(accountId) : null;
            MailUiState.SelectAccount(lastUsed ?? AccountStore.Accounts[0], restoreFolder: true);
            return true;
        }

        void HandleStateChanged()
        {
            if (TrySelectFirstAccount()) return;
            RefreshLists();
            _ = LoadOpenBodyIfNeededAsync();
            InvokeAsync(StateHasChanged);
        }

        void HandleSyncStateChanged() => InvokeAsync(StateHasChanged);

        /// <summary>Refreshes queued-move visibility on the renderer; failures bring the retained source row back.</summary>
        void HandleMoveStateChanged() => InvokeAsync(() => { RefreshLists(); StateHasChanged(); });

        /// <summary>Hover text of an account row: the keyboard shortcut plus how its syncing is going.</summary>
        internal static string SyncHealthTooltip(MailAccountData account)
        {
            var health = SyncHealthService.For(account.Id);
            if (!string.IsNullOrWhiteSpace(account.Title) || !string.IsNullOrWhiteSpace(account.Note))
            {
                string status = health.IsFailing
                    ? $"Failed {health.ConsecutiveFailures}× — {health.LastErrorMessage}"
                    : health.LastSuccessUtc is { } s ? $"Last synced {s.ToLocalTime():HH:mm}" : "Not synced yet";
                var lines = new List<string>(4);
                if (!string.IsNullOrWhiteSpace(account.Title)) lines.Add($"🏷️ {account.Title}");
                if (!string.IsNullOrWhiteSpace(account.Note)) lines.Add(account.Note);
                lines.Add(status);
                return string.Join("\n", lines);
            }
            if (health.IsFailing)
                return $"Failed {health.ConsecutiveFailures}× — {health.LastErrorMessage}";
            return health.LastSuccessUtc is { } success
                ? $"Last synced {success.ToLocalTime():HH:mm}"
                : "Not synced yet";
        }

        void RefreshLists()
        {
            var account = MailUiState.SelectedAccount;
            Folders = account == null ? [] : FolderTreeService.Build(MessageStore.GetFolders(account.Id));

            if (account != null && SearchAllFolders && !string.IsNullOrWhiteSpace(SearchText))
            {
                var hits = SearchService.Search(account.Id, SearchText)
                    .Where(hit => !MessageActions.IsMoving(account.Id, hit.FolderFullName, hit.Summary.Uid)).ToList();
                HitFolders = hits.ToDictionary(h => h.Summary, h => (h.FolderFullName, h.FolderDisplayName));
                FilteredSummaries = [.. hits.Select(h => h.Summary)];
                BuildRows();
                return;
            }

            HitFolders = [];
            var folder = MailUiState.SelectedFolder;
            if (account == null || folder == null)
            {
                FilteredSummaries = [];
                BuildRows();
                return;
            }

            // The matcher runs even for an empty query: it is also what hides snoozed mail.
            FilteredSummaries = [.. MessageStore.GetSummaries(account.Id, folder.FullName)
                .Where(SearchService.BuildMatcher(SearchText, account.Id))
                .Where(summary => !MessageActions.IsMoving(account.Id, folder.FullName, summary.Uid))];
            BuildRows();
        }

        /// <summary>
        /// The rows the list actually renders. Conversation mode collapses the summaries into one
        /// row per thread; flat mode maps them one-to-one. Both feed the SAME row markup, so the
        /// two modes can never drift apart visually.
        /// </summary>
        internal List<MailRow> Rows { get; set; } = [];

        /// <summary>Grouping is on when the ConversationView setting says so and no free-text search
        /// is narrowing the list; a pure-token query (is:unread) keeps conversations grouped.</summary>
        bool ConversationMode =>
            MailUiState.SelectedAccount is { } account
            && AccountStore.GetSettings(account.Id).ConversationView.Value
            && !SearchService.HasFreeText(SearchText);

        Dictionary<MailMessageSummary, IReadOnlyList<MailMessageSummary>> RowMessages = [];

        void BuildRows()
        {
            Rows = ConversationMode
                ? [.. ThreadingService.BuildThreads(FilteredSummaries).Select(static thread => new MailRow(
                    thread.Newest,
                    thread.Messages.Count,
                    thread.UnreadCount,
                    string.Join(", ", thread.Messages.Select(static m => string.IsNullOrWhiteSpace(m.FromName) ? m.FromAddress : m.FromName).Distinct())) { Messages = thread.Messages })]
                : [.. FilteredSummaries.Select(static summary => new MailRow(
                    summary,
                    1,
                    summary.IsUnread ? 1 : 0,
                    string.IsNullOrWhiteSpace(summary.FromName) ? summary.FromAddress : summary.FromName))];
            RowMessages = Rows.SelectMany(static row => row.Messages.Select(message => (Message: message, Members: row.Messages)))
                .ToDictionary(static pair => pair.Message, static pair => pair.Members);
        }

        void ToggleConversationMode()
        {
            if (MailUiState.SelectedAccount is not { } account) return;
            var conversationView = AccountStore.GetSettings(account.Id).ConversationView;
            conversationView.Value = !conversationView.Value;
            AccountStore.GetSettings(account.Id).Save();
            RefreshLists();
        }

        List<SearchRescue> SearchRescues =>
            MailUiState.SelectedAccount is { } account && MailUiState.SelectedFolder is { } folder
            && !string.IsNullOrWhiteSpace(SearchText)
                ? SearchRescueService.Suggest(account.Id, folder.FullName, SearchText, SearchAllFolders)
                : [];

        void ApplyRescue(SearchRescue rescue)
        {
            SearchAllFolders = rescue.SearchAllFolders;
            SearchText = rescue.Query;
        }

        void RunEmptyFolderAction(MailFolderData folder, EmptyFolderAction action)
        {
            FolderEmptyReason.RunAction(folder, action);
            StateHasChanged();
        }

        internal static string FolderFreshness(MailFolderData folder)
        {
            if (folder.IsLocal) return "Local folder — nothing to sync";
            if (folder.LastSyncedUtc is not { } synced) return "Messages not fetched yet";

            var age = DateTime.UtcNow - synced;
            return age switch
            {
                { TotalMinutes: < 2 } => "Messages up to date",
                { TotalHours: < 1 } => $"Messages from {(int)age.TotalMinutes} minutes ago",
                { TotalDays: < 1 } => $"Messages from {(int)age.TotalHours} hours ago",
                _ => $"Messages from {(int)age.TotalDays} days ago"
            };
        }

        /// <summary>Total unread across the account's folders (Trash/Junk excluded so the badge means real mail).</summary>
        internal static int AccountUnreadCount(string accountId) =>
            MessageStore.GetFolders(accountId)
                .Where(f => f.Role != FolderRole.Junk && FolderSettingsStore.ShowsUnreadCount(f))
                .Sum(f => f.UnreadCount);

        /// <summary>The folder a listed message actually lives in (differs from the selection in all-folders search).</summary>
        internal string? ResolveFolderOf(MailMessageSummary message) =>
            MessageIdentityOf(message)?.FolderFullName;

        string? HitFolderDisplay(MailMessageSummary message) =>
            HitFolders.TryGetValue(message, out var hit) ? hit.Display : null;

        void ToggleSearchAllFolders()
        {
            SearchAllFolders = !SearchAllFolders;
            RefreshLists();
        }

        internal void SearchByTag(string tagName) => RunSavedSearch($"tag:{tagName}");

        internal const int BackfillBatchSize = 300;

        int CachedCount => MailUiState.SelectedFolder is { } folder
            ? MessageStore.CountSummaries(folder.AccountId, folder.FullName)
            : 0;

        MailFolderData? TruncatedFolder =>
            MailUiState.SelectedFolder is { IsLocal: false } folder && CachedCount < folder.TotalCount ? folder : null;

        void LoadOlder(MailFolderData folder)
        {
            if (AccountStore.GetById(folder.AccountId) is not { } account) return;
            ImapSyncService.KickFolderSync(account, folder.FullName, backfillCount: BackfillBatchSize);
        }

        internal void ResyncFolder(MailFolderData folder)
        {
            if (AccountStore.GetById(folder.AccountId) is not { } account) return;
            MailUiState.SelectFolder(folder);
            ImapSyncService.KickFolderSync(account, folder.FullName, resetCache: true);
        }

        static readonly (string Label, string Token)[] QuickFilters =
        [
            ("📩 Unread", "is:unread"),
            ("⭐ Starred", "is:starred"),
            ("❗ Important", "is:important"),
            ("📎 Attachments", "has:attachment"),
            ("💤 Snoozed", "is:snoozed"),
            ("📅 Last 7 days", "newer_than:7d"),
            ("🔇 Muted", "is:muted")
        ];

        bool ShowSearchTools { get; set; }

        int ActiveFilterCount =>
            QuickFilters.Count(filter => HasSearchToken(filter.Token))
            + (ConversationMode ? 1 : 0)
            + (SearchAllFolders && !string.IsNullOrWhiteSpace(SearchText) ? 1 : 0);

        bool HasSearchToken(string token) =>
            SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part.Equals(token, StringComparison.OrdinalIgnoreCase));

        void ToggleSearchToken(string token)
        {
            var parts = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (parts.RemoveAll(part => part.Equals(token, StringComparison.OrdinalIgnoreCase)) == 0)
                parts.Add(token);
            SearchText = string.Join(' ', parts);
        }

        internal static List<string> SavedSearchList => [.. GlobalSettings.SavedSearches.Value.Split('\u001F', StringSplitOptions.RemoveEmptyEntries)];

        void SaveCurrentSearch()
        {
            if (string.IsNullOrWhiteSpace(SearchText)) return;
            var saved = SavedSearchList;
            if (saved.Contains(SearchText)) return;
            saved.Add(SearchText);
            PersistSavedSearches(saved);
        }

        internal static void RemoveSavedSearch(string query)
        {
            var saved = SavedSearchList;
            if (saved.Remove(query))
                PersistSavedSearches(saved);
        }

        static void PersistSavedSearches(List<string> saved)
        {
            GlobalSettings.SavedSearches.Set(string.Join('\u001F', saved));
            SettingsManager.SaveSettings();
        }

        internal void RunSavedSearch(string query)
        {
            _searchText = query;
            SearchAllFolders = true;
            RefreshLists();
        }

        const string LocalFolderIcon = "💻";
        const string LocalFolderSuffix = "(this PC)";

        internal static string FolderIcon(MailFolderData folder) =>
            folder.IsLocal ? LocalFolderIcon : FolderIcon(folder.Role);

        internal static string FolderLabel(MailFolderData folder) =>
            folder.IsLocal ? $"{folder.DisplayName} {LocalFolderSuffix}" : folder.DisplayName;

        internal static string FolderIcon(FolderRole role) => role switch
        {
            FolderRole.Inbox => "📥",
            FolderRole.Sent => "📤",
            FolderRole.Drafts => "📝",
            FolderRole.Trash => "🗑️",
            FolderRole.Junk => "🚫",
            FolderRole.Archive => "📦",
            FolderRole.Outbox => "⏳",
            FolderRole.Flagged => "⭐",
            FolderRole.AllMail => "🗂️",
            _ => "📁"
        };
    }
}
