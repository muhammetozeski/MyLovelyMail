using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.UI.Pages
{
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

        protected override void OnInitialized()
        {
            base.OnInitialized();
            MailUiState.OnSelectionChanged += HandleStateChanged;
            AccountStore.OnAccountsChanged += HandleStateChanged;
            MessageStore.OnFolderChanged += HandleFolderChanged;

            if (MailUiState.SelectedAccount == null && AccountStore.Accounts.Count > 0)
                SelectAccount(AccountStore.Accounts[0]);
            else
                RefreshLists();
        }

        void SelectAccount(MailAccountData account)
        {
            MailUiState.SelectAccount(account);
            Folders = SortFolders(MessageStore.GetFolders(account.Id));

            var inbox = Folders.FirstOrDefault(f => f.Role == FolderRole.Inbox) ?? Folders.FirstOrDefault();
            if (inbox != null)
                MailUiState.SelectFolder(inbox);
        }

        void HandleStateChanged()
        {
            RefreshLists();
            InvokeAsync(StateHasChanged);
        }

        void HandleFolderChanged(string accountId, string folderFullName)
        {
            if (MailUiState.SelectedAccount?.Id != accountId) return;
            RefreshLists();
            InvokeAsync(StateHasChanged);
        }

        void RefreshLists()
        {
            var account = MailUiState.SelectedAccount;
            Folders = account == null ? [] : SortFolders(MessageStore.GetFolders(account.Id));

            var folder = MailUiState.SelectedFolder;
            if (account == null || folder == null)
            {
                FilteredSummaries = [];
                return;
            }

            var summaries = MessageStore.GetSummaries(account.Id, folder.FullName);
            FilteredSummaries = string.IsNullOrWhiteSpace(SearchText)
                ? summaries
                : [.. summaries.Where(MatchesSearch)];
        }

        bool MatchesSearch(MailMessageSummary message) =>
            message.Subject.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || message.FromName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || message.FromAddress.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || message.PreviewText.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

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

        /// <summary>Today → clock; this year → day+month; older → full date.</summary>
        static string FormatDate(DateTime dateUtc)
        {
            var local = dateUtc.ToLocalTime();
            var now = DateTime.Now;
            if (local.Date == now.Date) return local.ToString("HH:mm");
            if (local.Year == now.Year) return local.ToString("dd MMM");
            return local.ToString("dd MMM yyyy");
        }

        public void Dispose()
        {
            MailUiState.OnSelectionChanged -= HandleStateChanged;
            AccountStore.OnAccountsChanged -= HandleStateChanged;
            MessageStore.OnFolderChanged -= HandleFolderChanged;
        }
    }
}
