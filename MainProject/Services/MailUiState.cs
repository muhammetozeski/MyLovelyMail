using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services
{
    /// <summary>
    /// The mail screen's shared selection state: which account, folder and message are open.
    /// Components read these and subscribe to <see cref="OnSelectionChanged"/> so the sidebar,
    /// list and reader stay in sync without passing parameters through the whole tree.
    /// </summary>
    public static class MailUiState
    {
        public static MailAccountData? SelectedAccount { get; private set; }
        public static MailFolderData? SelectedFolder { get; private set; }
        public static MailMessageSummary? OpenMessage { get; private set; }

        /// <summary>Row highlighted by keyboard navigation (independent of the opened message).</summary>
        public static MailMessageSummary? FocusedMessage { get; private set; }

        /// <summary>The draft open in the compose pane; null = compose pane closed.</summary>
        public static ComposeDraft? ActiveCompose { get; private set; }

        public static event Action? OnSelectionChanged;

        public static void OpenCompose(ComposeDraft draft)
        {
            ActiveCompose = draft;
            OnSelectionChanged?.Invoke();
        }

        public static void CloseCompose()
        {
            ActiveCompose = null;
            OnSelectionChanged?.Invoke();
        }

        public static void FocusMessage(MailMessageSummary? message)
        {
            FocusedMessage = message;
            OnSelectionChanged?.Invoke();
        }

        /// <summary>Uids checked for bulk actions in the CURRENT folder (cleared on any folder/account change).</summary>
        public static readonly HashSet<uint> SelectedUids = [];

        public static void SelectAccount(MailAccountData? account)
        {
            SelectedAccount = account;
            SelectedFolder = null;
            OpenMessage = null;
            SelectedUids.Clear();
            OnSelectionChanged?.Invoke();
        }

        public static void SelectFolder(MailFolderData? folder)
        {
            SelectedFolder = folder;
            OpenMessage = null;
            SelectedUids.Clear();
            OnSelectionChanged?.Invoke();

            // Every folder change in the app comes through here, so this is the one place that
            // has to record where the user is; the sidebar, Ctrl+1..9 and the debug API all inherit it.
            if (folder != null)
                FolderMemoryStore.Remember(folder.AccountId, folder.FullName);

            // The scheduled pass only fills the Inbox, so any other server folder is fetched
            // the moment the user opens it (incremental — repeat visits only pull what's new).
            if (folder is { IsLocal: false }
                && AccountStore.GetById(folder.AccountId) is { Enabled: true, Protocol: IncomingProtocol.Imap } account)
                ImapSyncService.KickFolderSync(account, folder.FullName);
        }

        public static void ToggleSelected(uint uid)
        {
            if (!SelectedUids.Remove(uid))
                SelectedUids.Add(uid);
            OnSelectionChanged?.Invoke();
        }

        public static void SelectMany(IEnumerable<uint> uids)
        {
            SelectedUids.UnionWith(uids);
            OnSelectionChanged?.Invoke();
        }

        public static void ClearSelection()
        {
            if (SelectedUids.Count == 0) return;
            SelectedUids.Clear();
            OnSelectionChanged?.Invoke();
        }

        public static void OpenMessageInReader(MailMessageSummary message)
        {
            OpenMessage = message;
            OnSelectionChanged?.Invoke();
        }

        public static void CloseMessage()
        {
            OpenMessage = null;
            OnSelectionChanged?.Invoke();
        }
    }
}
