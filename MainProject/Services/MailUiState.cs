using MyLovelyMail.MainProject.DataModels.Mail;

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

        public static event Action? OnSelectionChanged;

        public static void FocusMessage(MailMessageSummary? message)
        {
            FocusedMessage = message;
            OnSelectionChanged?.Invoke();
        }

        public static void SelectAccount(MailAccountData? account)
        {
            SelectedAccount = account;
            SelectedFolder = null;
            OpenMessage = null;
            OnSelectionChanged?.Invoke();
        }

        public static void SelectFolder(MailFolderData? folder)
        {
            SelectedFolder = folder;
            OpenMessage = null;
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
