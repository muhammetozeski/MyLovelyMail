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

        List<MailFolderData> Folders { get; set; } = [];

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
        }

        void SelectAccount(MailAccountData account)
        {
            MailUiState.SelectAccount(account);
            Folders = SortFolders(MessageStore.GetFolders(account.Id));

            var inbox = Folders.FirstOrDefault(f => f.Role == FolderRole.Inbox) ?? Folders.FirstOrDefault();
            if (inbox != null)
                MailUiState.SelectFolder(inbox);
        }

        /// <summary>Selects the first account when none is selected yet; returns whether it did.</summary>
        bool TrySelectFirstAccount()
        {
            if (MailUiState.SelectedAccount != null || AccountStore.Accounts.Count == 0) return false;
            SelectAccount(AccountStore.Accounts[0]);
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

            var summaries = MessageStore.GetSummaries(account.Id, folder.FullName);
            FilteredSummaries = string.IsNullOrWhiteSpace(SearchText)
                ? summaries
                : [.. summaries.Where(SearchService.BuildMatcher(SearchText))];
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

        /// <summary>One-click filters; each just adds or removes its own token in the search box, so they compose with each other and with typed text.</summary>
        static readonly (string Label, string Token)[] QuickFilters =
        [
            ("📩 Unread", "is:unread"),
            ("⭐ Starred", "is:starred"),
            ("❗ Important", "is:important"),
            ("📎 Attachments", "has:attachment")
        ];

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

        /// <summary>Well-known folders first (Inbox on top), then the rest alphabetically.</summary>
        static List<MailFolderData> SortFolders(List<MailFolderData> folders) =>
            [.. folders.OrderBy(f => RoleRank(f.Role)).ThenBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)];

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
