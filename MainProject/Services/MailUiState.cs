using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.Storage;

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
        /// <summary>Reader identity survives a search change that removes the opened message from the visible hits.</summary>
        public static MailMessageIdentity? OpenMessageKey { get; private set; }

        /// <summary>Row highlighted by keyboard navigation (independent of the opened message).</summary>
        public static MailMessageSummary? FocusedMessage { get; private set; }
        /// <summary>Complete identity of the keyboard focus, independent of the current search result.</summary>
        public static MailMessageIdentity? FocusedMessageKey { get; private set; }

        /// <summary>The draft open in the compose pane; null = compose pane closed.</summary>
        public static ComposeDraft? ActiveCompose { get; private set; }

        /// <summary>What the third column is showing. Three different things can live there and its
        /// visibility rule only ever asked about one of them.</summary>
        public enum PaneOccupant { Reader, Compose, Person }

        /// <summary>The occupant on screen, or null when the pane is empty.</summary>
        public static PaneOccupant? ThirdPane { get; private set; }

        /// <summary>Whichever occupant still has state behind it, so closing one falls back instead of blanking.</summary>
        static PaneOccupant? SurvivingOccupant() =>
            ActiveCompose != null ? PaneOccupant.Compose
            : OpenMessage != null ? PaneOccupant.Reader
            : OpenPersonAddress != null ? PaneOccupant.Person
            : null;

        /// <summary>Switches which occupant is shown without disturbing the other two.</summary>
        public static void ShowPane(PaneOccupant occupant)
        {
            ThirdPane = occupant;
            OnSelectionChanged?.Invoke();
        }

        public static event Action? OnSelectionChanged;

        public static void OpenCompose(ComposeDraft draft)
        {
            // Switching compose targets must preserve the unsent draft, including menu-driven replies.
            if (ActiveCompose is { } previous && previous.DraftId != draft.DraftId)
                ComposeService.SaveDraft(previous);
            ActiveCompose = draft;
            ThirdPane = PaneOccupant.Compose;
            OnSelectionChanged?.Invoke();
        }

        /// <summary>The correspondent whose sheet is open; null = closed.</summary>
        public static string? OpenPersonAddress { get; private set; }

        public static void OpenPerson(string address)
        {
            OpenPersonAddress = address;
            ThirdPane = PaneOccupant.Person;
            OnSelectionChanged?.Invoke();
        }

        public static void ClosePerson()
        {
            OpenPersonAddress = null;
            ThirdPane = SurvivingOccupant();
            OnSelectionChanged?.Invoke();
        }

        public static void CloseCompose()
        {
            ActiveCompose = null;
            ThirdPane = SurvivingOccupant();
            OnSelectionChanged?.Invoke();
        }

        public static void FocusMessage(MailMessageSummary? message, MailMessageIdentity? identity = null)
        {
            FocusedMessage = message;
            FocusedMessageKey = identity ?? (message != null && SelectedAccount is { } account && SelectedFolder is { } folder
                ? new MailMessageIdentity(account.Id, folder.FullName, message.Uid) : null);
            OnSelectionChanged?.Invoke();
        }

        /// <summary>Checked mail retains its account and folder, including in cross-folder search results.</summary>
        public static readonly HashSet<MailMessageIdentity> SelectedMessages = [];

        /// <summary>Changes account and optionally restores its remembered folder through the same selection path.</summary>
        /// <param name="account">Selected account, or null to clear it.</param>
        /// <param name="restoreFolder">Uses folder memory, then the existing Inbox/first-folder fallback.</param>
        public static void SelectAccount(MailAccountData? account, bool restoreFolder = false)
        {
            SelectedAccount = account;
            SelectedFolder = null;
            OpenMessage = null;
            OpenMessageKey = null;
            // The pane must not keep pointing at a message that is no longer listed.
            ThirdPane = SurvivingOccupant();
            FocusedMessage = null;
            FocusedMessageKey = null;
            SelectedMessages.Clear();
            OnSelectionChanged?.Invoke();
            if (restoreFolder && account != null && FolderMemoryStore.ResolveStartFolder(FolderMemoryStore.FolderOf(account.Id),
                MessageStore.GetFolders(account.Id)) is { } target) SelectFolder(target);
        }

        public static void SelectFolder(MailFolderData? folder)
        {
            SelectedFolder = folder;
            OpenMessage = null;
            OpenMessageKey = null;
            ThirdPane = SurvivingOccupant();
            FocusedMessage = null;
            FocusedMessageKey = null;
            SelectedMessages.Clear();
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

        /// <summary>Maps the existing development endpoint's folder-local numbers to complete identities.</summary>
        /// <param name="uids">Message numbers in the currently selected folder.</param>
        public static void SelectMany(IEnumerable<uint> uids)
        {
            if (SelectedAccount is { } account && SelectedFolder is { } folder)
                SelectMessages(uids.Select(uid => new MailMessageIdentity(account.Id, folder.FullName, uid)));
        }

        /// <summary>Applies checkbox, range and select-all changes through the same selection update.</summary>
        /// <param name="messages">Complete identities, expanded by the caller for conversation rows.</param>
        /// <param name="selected">Whether to add or remove them.</param>
        /// <param name="replace">Clears the earlier selection before applying the change.</param>
        public static void SelectMessages(IEnumerable<MailMessageIdentity> messages, bool selected = true, bool replace = false)
        {
            if (replace) SelectedMessages.Clear();
            if (selected) SelectedMessages.UnionWith(messages);
            else SelectedMessages.ExceptWith(messages);
            OnSelectionChanged?.Invoke();
        }

        public static void ClearSelection()
        {
            if (SelectedMessages.Count == 0) return;
            SelectedMessages.Clear();
            OnSelectionChanged?.Invoke();
        }

        public static void OpenMessageInReader(MailMessageSummary message, MailMessageIdentity? identity = null)
        {
            OpenMessage = message;
            OpenMessageKey = identity ?? (SelectedAccount is { } account && SelectedFolder is { } folder
                ? new MailMessageIdentity(account.Id, folder.FullName, message.Uid) : null);
            ThirdPane = PaneOccupant.Reader;
            OnSelectionChanged?.Invoke();
        }

        public static void CloseMessage()
        {
            OpenMessage = null;
            OpenMessageKey = null;
            ThirdPane = SurvivingOccupant();
            OnSelectionChanged?.Invoke();
        }
    }
}
