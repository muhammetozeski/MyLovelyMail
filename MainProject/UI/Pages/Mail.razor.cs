using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using GlobalSettings = MyLovelyMail.MainProject.Stores.Settings;

namespace MyLovelyMail.MainProject.UI.Pages
{
    /// <summary>One rendered list row. <paramref name="ConversationCount"/> is 1 in flat mode, so the
    /// same markup serves both modes; the count pill appears only above 1.</summary>
    public sealed record MailRow(MailMessageSummary Message, int ConversationCount, int UnreadCount, string SenderText);

    // Core of the mail screen: lifecycle/event wiring plus the folder list, message list and
    // search. The reader, compose and interaction members live in the sibling partials.
    public partial class Mail
    {
        string _searchText = string.Empty;

        string SearchText
        {
            get => _searchText;
            set
            {
                _searchText = value;
                RefreshLists();
            }
        }

        /// <summary>The folder rows in display order, each carrying its depth in the server's tree.</summary>
        List<FolderNode> Folders { get; set; } = [];

        /// <summary>The open folder's summaries after the search box filter, newest first.</summary>
        List<MailMessageSummary> FilteredSummaries { get; set; } = [];

        bool SearchAllFolders { get; set; }

        /// <summary>Search-hit summary → (folder full name, display name), filled only in all-folders mode.</summary>
        Dictionary<MailMessageSummary, (string FullName, string Display)> HitFolders { get; set; } = [];

        protected override void OnInitialized()
        {
            base.OnInitialized();
            MailUiState.OnSelectionChanged += HandleStateChanged;
            AccountStore.OnAccountsChanged += HandleStateChanged;
            MessageStore.OnFolderChanged += HandleFolderChanged;
            SyncScheduler.OnSyncStateChanged += HandleSyncStateChanged;
            ImapSyncService.OnFolderSyncStateChanged += HandleSyncStateChanged;
            OutboxService.OnChanged += HandleOutboxChanged;
            MouseActionService.OnStatus += HandleMouseStatus;

            if (!TrySelectFirstAccount())
                RefreshLists();
        }

        public void Dispose()
        {
            draftAutosaveTimer?.Dispose();
            undoCountdownTimer?.Dispose();
            MailUiState.OnSelectionChanged -= HandleStateChanged;
            AccountStore.OnAccountsChanged -= HandleStateChanged;
            MessageStore.OnFolderChanged -= HandleFolderChanged;
            SyncScheduler.OnSyncStateChanged -= HandleSyncStateChanged;
            ImapSyncService.OnFolderSyncStateChanged -= HandleSyncStateChanged;
            OutboxService.OnChanged -= HandleOutboxChanged;
            MouseActionService.OnStatus -= HandleMouseStatus;
        }

        /// <summary>A side-button action reports into the sidebar line — nothing the mouse does happens silently.</summary>
        void HandleMouseStatus(string status)
        {
            FolderActionStatus = status;
            InvokeAsync(StateHasChanged);
        }

        void SelectAccount(MailAccountData account)
        {
            MailUiState.SelectAccount(account);
            Folders = SortFolders(MessageStore.GetFolders(account.Id));

            if (FolderMemoryStore.ResolveStartFolder(FolderMemoryStore.FolderOf(account.Id), [.. Folders.Select(static n => n.Folder)]) is { } target)
                MailUiState.SelectFolder(target);
        }

        /// <summary>Selects an account when none is selected yet — the one last used, else the first.</summary>
        bool TrySelectFirstAccount()
        {
            if (MailUiState.SelectedAccount != null || AccountStore.Accounts.Count == 0) return false;
            var lastUsed = FolderMemoryStore.LastAccountId is { } accountId ? AccountStore.GetById(accountId) : null;
            SelectAccount(lastUsed ?? AccountStore.Accounts[0]);
            return true;
        }

        void HandleStateChanged()
        {
            // An account added while the page is open (wizard save, debug API) selects itself,
            // so folders and the compose button appear without re-entering the page.
            if (TrySelectFirstAccount()) return;
            RefreshLists();
            _ = LoadOpenBodyIfNeededAsync();
            InvokeAsync(StateHasChanged);
        }

        void HandleSyncStateChanged() => InvokeAsync(StateHasChanged);

        /// <summary>Hover text of an account row: the keyboard shortcut plus how its syncing is going.</summary>
        static string SyncHealthTooltip(MailAccountData account)
        {
            var health = SyncHealthService.For(account.Id);
            if (health.IsFailing)
                return $"Failed {health.ConsecutiveFailures}× — {health.LastErrorMessage}";
            return health.LastSuccessUtc is { } success
                ? $"Last synced {success.ToLocalTime():HH:mm}"
                : "Not synced yet";
        }

        void RefreshLists()
        {
            var account = MailUiState.SelectedAccount;
            Folders = account == null ? [] : SortFolders(MessageStore.GetFolders(account.Id));

            if (account != null && SearchAllFolders && !string.IsNullOrWhiteSpace(SearchText))
            {
                var hits = SearchService.Search(account.Id, SearchText);
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
                .Where(SearchService.BuildMatcher(SearchText, account.Id))];
            BuildRows();
        }

        /// <summary>
        /// The rows the list actually renders. Conversation mode collapses the summaries into one
        /// row per thread; flat mode maps them one-to-one. Both feed the SAME row markup, so the
        /// two modes can never drift apart visually.
        /// </summary>
        List<MailRow> Rows { get; set; } = [];

        /// <summary>Grouping is on when the ConversationView setting says so and no free-text search
        /// is narrowing the list; a pure-token query (is:unread) keeps conversations grouped.</summary>
        bool ConversationMode =>
            MailUiState.SelectedAccount is { } account
            && AccountStore.GetSettings(account.Id).ConversationView.Value
            && !SearchService.HasFreeText(SearchText);

        void BuildRows() =>
            Rows = ConversationMode
                ? [.. ThreadingService.BuildThreads(FilteredSummaries).Select(static thread => new MailRow(
                    thread.Newest,
                    thread.Messages.Count,
                    thread.UnreadCount,
                    string.Join(", ", thread.Messages.Select(static m => string.IsNullOrWhiteSpace(m.FromName) ? m.FromAddress : m.FromName).Distinct()) ))]
                : [.. FilteredSummaries.Select(static summary => new MailRow(
                    summary,
                    1,
                    summary.IsUnread ? 1 : 0,
                    string.IsNullOrWhiteSpace(summary.FromName) ? summary.FromAddress : summary.FromName))];

        void ToggleConversationMode()
        {
            if (MailUiState.SelectedAccount is not { } account) return;
            var conversationView = AccountStore.GetSettings(account.Id).ConversationView;
            conversationView.Value = !conversationView.Value;
            AccountStore.GetSettings(account.Id).Save();
            RefreshLists();
        }

        /// <summary>
        /// Ways to get results back, computed only on the empty-result screen — the scan runs the
        /// real matcher several times over the folder, which is nothing next to a wall of "no
        /// results" but is not something to do on every render of a list that has rows.
        /// </summary>
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

        /// <summary>
        /// Hover text answering "how old is what I am looking at". The folder-list pass keeps every
        /// unread badge current while the messages behind it can be weeks old, so the badge alone
        /// cannot tell the user that.
        /// </summary>
        static string FolderFreshness(MailFolderData folder)
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
        static int AccountUnreadCount(string accountId) =>
            MessageStore.GetFolders(accountId)
                .Where(f => f.Role is not (FolderRole.Trash or FolderRole.Junk))
                .Sum(f => f.UnreadCount);

        /// <summary>The folder a listed message actually lives in (differs from the selection in all-folders search).</summary>
        string? ResolveFolderOf(MailMessageSummary message) =>
            HitFolders.TryGetValue(message, out var hit) ? hit.FullName : MailUiState.SelectedFolder?.FullName;

        string? HitFolderDisplay(MailMessageSummary message) =>
            HitFolders.TryGetValue(message, out var hit) ? hit.Display : null;

        void ToggleSearchAllFolders()
        {
            SearchAllFolders = !SearchAllFolders;
            RefreshLists();
        }

        void SearchByTag(string tagName) => RunSavedSearch($"tag:{tagName}");

        /// <summary>How many older messages one press of the footer button asks for.</summary>
        internal const int BackfillBatchSize = 300;

        /// <summary>Messages cached for the open folder — the honest half of "showing X of Y".</summary>
        int CachedCount => MailUiState.SelectedFolder is { } folder
            ? MessageStore.GetSummaries(folder.AccountId, folder.FullName).Count
            : 0;

        /// <summary>The open folder when the server holds more than the cache does, else null.</summary>
        MailFolderData? TruncatedFolder =>
            MailUiState.SelectedFolder is { IsLocal: false } folder && CachedCount < folder.TotalCount ? folder : null;

        void LoadOlder(MailFolderData folder)
        {
            if (AccountStore.GetById(folder.AccountId) is not { } account) return;
            ImapSyncService.KickFolderBackfill(account, folder.FullName, BackfillBatchSize);
        }

        /// <summary>Drops the folder's cache and refills it — the way back from a truncated folder that incremental sync can never heal.</summary>
        void ResyncFolder(MailFolderData folder)
        {
            if (AccountStore.GetById(folder.AccountId) is not { } account) return;
            MailUiState.SelectFolder(folder);
            ImapSyncService.KickFolderResync(account, folder.FullName);
        }

        /// <summary>One-click filters; each just adds or removes its own token in the search box, so they compose with each other and with typed text.</summary>
        static readonly (string Label, string Token)[] QuickFilters =
        [
            ("📩 Unread", "is:unread"),
            ("⭐ Starred", "is:starred"),
            ("❗ Important", "is:important"),
            ("📎 Attachments", "has:attachment"),
            ("💤 Snoozed", "is:snoozed"),
            ("🕒 Last 7 days", "newer_than:7d"),
            ("🔕 Muted", "is:muted")
        ];

        /// <summary>
        /// Whether the quick-filter pill row is unfolded. Deliberately not persisted: every start
        /// gives the list its full height back, and one click brings the pills straight back.
        /// </summary>
        bool ShowSearchTools { get; set; }

        /// <summary>How many filters are actually narrowing the list right now - what the folded handle reports.</summary>
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

        static List<string> SavedSearchList => [.. GlobalSettings.SavedSearches.Value.Split('\u001F', StringSplitOptions.RemoveEmptyEntries)];

        void SaveCurrentSearch()
        {
            if (string.IsNullOrWhiteSpace(SearchText)) return;
            var saved = SavedSearchList;
            if (saved.Contains(SearchText)) return;
            saved.Add(SearchText);
            PersistSavedSearches(saved);
        }

        static void RemoveSavedSearch(string query)
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

        void RunSavedSearch(string query)
        {
            _searchText = query;
            SearchAllFolders = true;
            RefreshLists();
        }

        /// <summary>Display order and nesting depth; the ordering rules live in FolderTreeService now.</summary>
        static List<FolderNode> SortFolders(List<MailFolderData> folders) => FolderTreeService.Build(folders);

        static int RoleRank(FolderRole role) => role switch
        {
            FolderRole.Inbox => 0,
            FolderRole.Flagged => 1,
            FolderRole.Drafts => 2,
            FolderRole.Sent => 3,
            FolderRole.Outbox => 4,
            FolderRole.Archive => 5,
            FolderRole.Junk => 6,
            FolderRole.Trash => 7,
            FolderRole.AllMail => 8,
            _ => 9
        };

        /// <summary>Glyph for a folder that exists only on this machine.</summary>
        const string LocalFolderIcon = "💾";

        /// <summary>Appended to a local folder's name, because a server folder can carry the same one.</summary>
        const string LocalFolderSuffix = "(this PC)";

        /// <summary>
        /// The folder's icon. Local folders get their own glyph rather than their role's: rules and
        /// the draft autosave create "Local/Drafts" beside the server's "Drafts", and two rows
        /// showing 📝 Drafts are indistinguishable — including in the move menu, where picking the
        /// wrong one files the message somewhere the user did not mean.
        /// </summary>
        static string FolderIcon(MailFolderData folder) =>
            folder.IsLocal ? LocalFolderIcon : FolderIcon(folder.Role);

        /// <summary>What the folder is called on screen; local folders say where they live.</summary>
        static string FolderLabel(MailFolderData folder) =>
            folder.IsLocal ? $"{folder.DisplayName} {LocalFolderSuffix}" : folder.DisplayName;

        static string FolderIcon(FolderRole role) => role switch
        {
            FolderRole.Inbox => "📥",
            FolderRole.Sent => "📤",
            FolderRole.Drafts => "📝",
            FolderRole.Trash => "🗑️",
            FolderRole.Junk => "🚫",
            FolderRole.Archive => "📦",
            FolderRole.Outbox => "⏳",
            FolderRole.Flagged => "⭐",
            FolderRole.AllMail => "📚",
            _ => "📁"
        };
    }
}
