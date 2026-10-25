using Microsoft.AspNetCore.Components.Web;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;

namespace MyLovelyMail.MainProject.UI.Pages;

public partial class Mail
{
    bool SelectionMode;
    MailMessageIdentity? SelectionAnchor;

    /// <summary>Expands a row using its already-built conversation group; no second grouping algorithm is needed.</summary>
    /// <param name="message">Clicked row or reader message.</param>
    /// <returns>Complete identities behind that row.</returns>
    internal MailMessageIdentity[] MessageTargets(MailMessageSummary message) =>
        MessageIdentities(RowMessages.GetValueOrDefault(message) ?? [message]);

    /// <summary>Resolves and deduplicates cached messages using the page's complete identity map.</summary>
    /// <param name="messages">Visible or conversation member summaries.</param>
    /// <returns>Identities that can safely be passed to a shared mail command.</returns>
    MailMessageIdentity[] MessageIdentities(IEnumerable<MailMessageSummary> messages) =>
        [.. messages.Select(MessageIdentityOf).OfType<MailMessageIdentity>().Distinct()];

    /// <summary>Whether every member represented by a checkbox is selected.</summary>
    /// <param name="message">Row summary.</param>
    /// <returns>True for a fully selected row.</returns>
    internal bool IsRowSelected(MailMessageSummary message) =>
        MessageTargets(message) is { Length: > 0 } targets && targets.All(MailUiState.SelectedMessages.Contains);

    /// <summary>Checkboxes, Ctrl-click and selection mode share range and conversation expansion.</summary>
    /// <param name="pointer">Modifier keys from the click.</param>
    /// <param name="message">Row being selected.</param>
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

    /// <summary>Runs toolbar operations through the same typed command path as context menus and side buttons.</summary>
    /// <param name="command">Requested operation.</param>
    /// <param name="argument">Optional tag or folder.</param>
    /// <param name="dueUtc">Optional snooze deadline.</param>
    Task RunSelectionCommandAsync(MailMessageCommand command, string? argument = null, DateTime? dueUtc = null) =>
        MouseActionService.RunMessagesAsync(command, MailUiState.SelectedMessages.ToArray(), argument, dueUtc);
}
