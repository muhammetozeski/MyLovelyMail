using Microsoft.AspNetCore.Components.Web;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.UI.Pages;

public partial class Mail
{
    bool SelectionMode;
    MailMessageIdentity? SelectionAnchor;

    /// <summary>Expands a row using its already-built conversation group; no second grouping algorithm is needed.</summary>
    internal MailMessageIdentity[] MessageTargets(MailMessageSummary message) =>
        MessageIdentities(RowMessages.GetValueOrDefault(message) ?? [message]);

    /// <summary>Resolves and deduplicates cached messages using the page's complete identity map.</summary>
    MailMessageIdentity[] MessageIdentities(IEnumerable<MailMessageSummary> messages) =>
        [.. messages.Select(MessageIdentityOf).OfType<MailMessageIdentity>().Distinct()];

    /// <summary>Whether every member represented by a checkbox is selected.</summary>
    internal bool IsRowSelected(MailMessageSummary message) =>
        MessageTargets(message) is { Length: > 0 } targets && targets.All(MailUiState.SelectedMessages.Contains);

    /// <summary>Checkboxes, Ctrl-click and selection mode share range and conversation expansion.</summary>
    internal void ToggleRowSelection(MouseEventArgs pointer, MailMessageSummary message)
    {
        if (pointer.ShiftKey && SelectionAnchor is { } anchor)
        {
            int start = Rows.FindIndex(row => row.Messages.Any(member => MessageIdentityOf(member) == anchor));
            int end = Rows.FindIndex(row => ReferenceEquals(row.Message, message));
            if (start >= 0 && end >= 0)
            {
                MailUiState.SelectMessages(MessageIdentities(Rows.Skip(Math.Min(start, end))
                    .Take(Math.Abs(end - start) + 1).SelectMany(static row => row.Messages)), replace: !pointer.CtrlKey);
                return;
            }
        }
        var targets = MessageTargets(message);
        MailUiState.SelectMessages(targets, selected: !targets.All(MailUiState.SelectedMessages.Contains));
        SelectionAnchor = MessageIdentityOf(message);
    }

    /// <summary>Selects the messages currently represented by the list, including collapsed conversations.</summary>
    void SelectAllMessages() => MailUiState.SelectMessages(MessageIdentities(Rows.SelectMany(static row => row.Messages)), replace: true);

    /// <summary>Useful targets for side buttons over unassigned space in the mail workspace.</summary>
    MailMessageIdentity[] WorkspaceTargets => MailUiState.ThirdPane is MailUiState.PaneOccupant.Compose or MailUiState.PaneOccupant.Person ? []
        : MailUiState.SelectedMessages.Count > 0 ? [.. MailUiState.SelectedMessages]
        : MailUiState.OpenMessage is { } opened ? MessageTargets(opened)
        : MailUiState.FocusedMessage is { } focused ? MessageTargets(focused) : [];

    /// <summary>Returns valid destination folders for moving the given message identities.</summary>
    internal List<MailFolderData> MoveTargets(IEnumerable<MailMessageIdentity> targets)
    {
        var keys = targets.Distinct().ToArray();
        if (keys.Length == 0) return [];
        string accountId = keys[0].AccountId;
        if (keys.Any(k => k.AccountId != accountId) || AccountStore.GetById(accountId) is not { } account)
            return [];

        bool canMoveRemote = account.Protocol == IncomingProtocol.Imap && keys.All(key =>
            !MessageStore.IsLocalFolder(key.FolderFullName) && FolderSyncPolicy.PushesToServer(account.Id, key.FolderFullName));

        return [.. MessageStore.GetFolders(account.Id)
            .Where(folder => folder.Selectable && folder.Role is not (FolderRole.Drafts or FolderRole.Outbox))
            .Where(folder => keys.Any(key => key.FolderFullName != folder.FullName) && (folder.IsLocal || canMoveRemote))];
    }

    /// <summary>Runs toolbar operations through the same typed command path as context menus and side buttons.</summary>
    Task RunSelectionCommandAsync(MailMessageCommand command, string? argument = null, DateTime? dueUtc = null) =>
        MouseActionService.RunMessagesAsync(command, MailUiState.SelectedMessages.ToArray(), argument, dueUtc);
}
