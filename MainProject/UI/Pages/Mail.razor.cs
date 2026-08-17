using Microsoft.AspNetCore.Components.Web;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using GlobalSettings = MyLovelyMail.MainProject.Stores.Settings;

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
            _ = LoadOpenBodyIfNeededAsync();
            InvokeAsync(StateHasChanged);
        }

        string? OpenBodyHtml { get; set; }
        bool OpenBodyLoading { get; set; }
        string? BodyError { get; set; }
        uint loadedBodyUid;

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

            string? html = MailBodyRenderer.Render(account, folderName, open);
            if (html == null)
            {
                try
                {
                    if (account.Protocol == IncomingProtocol.Imap && !folderName.StartsWith(MessageStore.LocalFolderPrefix))
                        await ImapSyncService.DownloadMessageAsync(account, folderName, open.Uid);
                    else if (account.Protocol == IncomingProtocol.Pop3)
                        await Pop3Service.DownloadMessageAsync(account, open.Uid);
                    html = MailBodyRenderer.Render(account, folderName, open);
                }
                catch (Exception ex)
                {
                    BodyError = ex.Message;
                }
            }

            if (MailUiState.OpenMessage?.Uid != open.Uid) return;

            OpenBodyHtml = html;
            OpenBodyLoading = false;
            OpenAttachments = open.HasAttachments ? AttachmentService.List(account, folderName, open) : [];
            MarkOpenAsRead(account, folderName, open);
            await InvokeAsync(StateHasChanged);
        }

        List<AttachmentInfo> OpenAttachments { get; set; } = [];
        string? SaveStatus { get; set; }
        bool ShowTagPicker { get; set; }
        string NewTagName { get; set; } = string.Empty;

        void SearchByTag(string tagName) => RunSavedSearch($"tag:{tagName}");

        void ToggleTagOnOpen(MailMessageSummary message, string tagName)
        {
            if (MailUiState.SelectedAccount is { } account && ResolveFolderOf(message) is { } folderName)
                MessageActions.ToggleTag(account, folderName, message, tagName);
        }

        void AddNewTag(MailMessageSummary message)
        {
            string tagName = NewTagName.Trim();
            if (tagName.Length == 0) return;
            TagStore.ColorOf(tagName);
            ToggleTagOnOpen(message, tagName);
            NewTagName = string.Empty;
        }

        async Task SaveAttachmentAsync(int attachmentIndex)
        {
            if (MailUiState.SelectedAccount is not { } account || MailUiState.OpenMessage is not { } open) return;
            if (ResolveFolderOf(open) is not { } folderName) return;
            try
            {
                string path = await AttachmentService.SaveAsync(account, folderName, open, attachmentIndex);
                SaveStatus = $"💾 Saved to {path}";
            }
            catch (Exception ex)
            {
                SaveStatus = $"❌ {ex.Message}";
            }
        }

        async Task SaveAllAttachmentsAsync()
        {
            if (MailUiState.SelectedAccount is not { } account || MailUiState.OpenMessage is not { } open) return;
            if (ResolveFolderOf(open) is not { } folderName) return;
            try
            {
                string folder = await AttachmentService.SaveAllAsync(account, folderName, open);
                SaveStatus = $"💾 All attachments saved to {folder}";
            }
            catch (Exception ex)
            {
                SaveStatus = $"❌ {ex.Message}";
            }
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

        void HandleFolderChanged(string accountId, string folderFullName)
        {
            if (MailUiState.SelectedAccount?.Id != accountId) return;
            RefreshLists();
            InvokeAsync(StateHasChanged);
        }

        bool SearchAllFolders { get; set; }

        /// <summary>Search-hit summary → (folder full name, display name), filled only in all-folders mode.</summary>
        Dictionary<MailMessageSummary, (string FullName, string Display)> HitFolders { get; set; } = [];

        void RefreshLists()
        {
            var account = MailUiState.SelectedAccount;
            Folders = account == null ? [] : SortFolders(MessageStore.GetFolders(account.Id));

            if (account != null && SearchAllFolders && !string.IsNullOrWhiteSpace(SearchText))
            {
                var hits = SearchService.Search(account.Id, SearchText);
                HitFolders = hits.ToDictionary(h => h.Summary, h => (h.FolderFullName, h.FolderDisplayName));
                FilteredSummaries = [.. hits.Select(h => h.Summary)];
                return;
            }

            HitFolders = [];
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

        List<string> SavedSearchList => [.. GlobalSettings.SavedSearches.Value.Split('\u001F', StringSplitOptions.RemoveEmptyEntries)];

        void SaveCurrentSearch()
        {
            if (string.IsNullOrWhiteSpace(SearchText)) return;
            var saved = SavedSearchList;
            if (saved.Contains(SearchText)) return;
            saved.Add(SearchText);
            PersistSavedSearches(saved);
        }

        void RemoveSavedSearch(string query)
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

        bool ShowShortcutHelp { get; set; }
        bool Sending { get; set; }
        string? SendError { get; set; }

        static void StartCompose()
        {
            if (MailUiState.SelectedAccount is { } account)
                MailUiState.OpenCompose(ComposeService.BuildNew(account));
        }

        void StartReply(MailMessageSummary message, bool replyAll)
        {
            if (MailUiState.SelectedAccount is { } account && ResolveFolderOf(message) is { } folderName)
                MailUiState.OpenCompose(ComposeService.BuildReply(account, folderName, message, replyAll));
        }

        void StartForward(MailMessageSummary message)
        {
            if (MailUiState.SelectedAccount is { } account && ResolveFolderOf(message) is { } folderName)
                MailUiState.OpenCompose(ComposeService.BuildForward(account, folderName, message));
        }

        void ToggleOpenFlagged(MailMessageSummary message)
        {
            if (MailUiState.SelectedAccount is { } account && ResolveFolderOf(message) is { } folderName)
                MessageActions.ToggleFlagged(account, folderName, message);
        }

        void ToggleOpenImportant(MailMessageSummary message)
        {
            if (MailUiState.SelectedAccount is { } account && ResolveFolderOf(message) is { } folderName)
                MessageActions.ToggleImportant(account, folderName, message);
        }

        void DeleteOpen(MailMessageSummary message)
        {
            if (MailUiState.SelectedAccount is { } account && ResolveFolderOf(message) is { } folderName)
            {
                MessageActions.Delete(account, folderName, message);
                MailUiState.CloseMessage();
            }
        }

        async Task SendActiveDraftAsync()
        {
            if (MailUiState.ActiveCompose is not { } draft) return;
            Sending = true;
            SendError = null;
            StateHasChanged();
            try
            {
                await ComposeService.SendAsync(draft);
                MailUiState.CloseCompose();
            }
            catch (Exception ex)
            {
                SendError = ex.Message;
            }
            finally
            {
                Sending = false;
            }
        }

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
            ("?", "Show this help"),
            ("Search", "from: to: tag: has:attachment is:unread is:starred")
        ];

        static void OpenMessage(MailMessageSummary message)
        {
            MailUiState.FocusMessage(message);
            MailUiState.OpenMessageInReader(message);
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
                    else MailUiState.CloseMessage();
                    break;
                case "?":
                    ShowShortcutHelp = !ShowShortcutHelp;
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
