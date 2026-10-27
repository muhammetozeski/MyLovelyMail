using System.Text.Json;
using Microsoft.AspNetCore.Components.Web;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.UI.Components;

namespace MyLovelyMail.MainProject.UI.Pages;

public partial class Mail
{
    /// <summary>Handles a reader-frame request on the owning renderer with its captured identities and selection.</summary>
    /// <param name="request">Context collected by the shared input script.</param>
    Task HandleNativeMailMenuAsync(MouseMenuRequest request) => InvokeAsync(() =>
    {
        var keys = JsonSerializer.Deserialize<MailMessageIdentity[]>(request.MessagePayload) ?? [];
        MailMessageIdentity? primary = request.PrimaryPayload.Length == 0 ? null
            : JsonSerializer.Deserialize<MailMessageIdentity>(request.PrimaryPayload);
        OpenMessageMenu(keys, request.X, request.Y, request, primary);
    });

    /// <summary>Preserves a selection when right-clicking one of its members; an outside row becomes the new target.</summary>
    /// <param name="clicked">Complete messages represented by the clicked row or reader.</param>
    /// <param name="x">Viewport horizontal coordinate.</param>
    /// <param name="y">Viewport vertical coordinate.</param>
    /// <param name="native">Optional frame selection/link context.</param>
    /// <param name="primary">Specific reader member; otherwise the newest clicked message is used.</param>
    internal void OpenMessageMenu(IEnumerable<MailMessageIdentity> clicked, double x, double y,
        MouseMenuRequest? native = null, MailMessageIdentity? primary = null)
    {
        var clickedKeys = clicked.Distinct().ToArray();
        if (clickedKeys.Length == 0) return;
        MailUiState.SelectMessages(clickedKeys, replace: !clickedKeys.Any(MailUiState.SelectedMessages.Contains));
        var targets = MailUiState.SelectedMessages.ToArray();
        ContextMenuItem Command(string label, MailMessageCommand command, string? argument = null, DateTime? dueUtc = null) =>
            new(label, () => MouseActionService.RunMessagesAsync(command, targets, argument, dueUtc));
        List<ContextMenuItem> items =
        [
            Command("✅ Mark read · Mouse 5", MailMessageCommand.Read),
            Command("📩 Mark unread", MailMessageCommand.Unread),
            Command("🗑️ Move to Trash · Mouse 4", MailMessageCommand.Trash),
            Command("📦 Archive", MailMessageCommand.Archive),
            Command("🚫 Move to Spam", MailMessageCommand.Spam),
            Command("📥 Restore to Inbox (local for local mail)", MailMessageCommand.Restore),
            Command("⭐ Toggle stars", MailMessageCommand.ToggleStar),
            Command("❗ Toggle importance", MailMessageCommand.ToggleImportant)
        ];
        var moves = MoveTargets(targets);
        if (moves.Count > 0)
            items.Add(new("📂 Move to", Children: [.. moves.Select(folder => Command(FolderLabel(folder), MailMessageCommand.Move, folder.FullName))]));
        if (TagStore.AllTags.Count > 0)
        {
            items.Add(new("🏷️ Add tag", Children: [.. TagStore.AllTags.Select(tag => Command(tag, MailMessageCommand.AddTag, tag))]));
            items.Add(new("🏷️ Remove tag", Children: [.. TagStore.AllTags.Select(tag => Command(tag, MailMessageCommand.RemoveTag, tag))]));
        }
        items.Add(new("💤 Snooze", Children: [.. SnoozeService.Presets.Select(preset =>
            new ContextMenuItem(preset.Label, () => MouseActionService.RunMessagesAsync(MailMessageCommand.Snooze, targets, dueUtc: preset.DueUtc())))]));
        if (targets.Any(key => MessageStore.GetSummary(key.AccountId, key.FolderFullName, key.Uid)?.SnoozedUntilUtc != null))
            items.Add(Command("☀️ Wake now", MailMessageCommand.Wake));
        bool oneConversation = targets.Length == clickedKeys.Length && targets.All(clickedKeys.Contains);
        var lead = primary ?? clickedKeys.OrderByDescending(key => MessageStore.GetSummary(key.AccountId, key.FolderFullName, key.Uid)?.DateUtc).First();
        if (oneConversation)
        {
            items.AddRange([
                new("📖 Open message", () => OpenMessage(ResolveMenuMessage(lead))),
                new("↩️ Reply", () => StartReply(ResolveMenuMessage(lead), false)),
                new("👥 Reply all", () => StartReply(ResolveMenuMessage(lead), true)),
                new("➡️ Forward", () => StartForward(ResolveMenuMessage(lead))),
                new("📎 Forward as attachment", () => StartForwardAsAttachment(ResolveMenuMessage(lead))),
                new("💾 Save message as .eml", () => ExportOpen(ResolveMenuMessage(lead), false)),
                new("🌐 Save message as .html", () => ExportOpen(ResolveMenuMessage(lead), true)),
                new("🔍 View message source", () => OpenMessageSource(ResolveMenuMessage(lead))),
                new("⧉ Copy subject", () => MouseActionService.RunAsync(MouseActionService.CopyAction, ResolveMenuMessage(lead).Subject)),
                new("⧉ Copy sender address", () => MouseActionService.RunAsync(MouseActionService.CopyAction, ResolveMenuMessage(lead).FromAddress)),
                new("👤 Sender details", () => MailUiState.OpenPerson(ResolveMenuMessage(lead).FromAddress)),
                new("🔎 Find mail from this sender", () => RunSavedSearch("from:" + ResolveMenuMessage(lead).FromAddress))
            ]);
        }
        if (native?.Selection is { Length: > 0 } selected)
            items.Add(new("⧉ Copy selected text", () => MouseActionService.RunAsync(MouseActionService.CopyAction, selected)));
        if (native?.Link is { Length: > 0 } link)
        {
            items.Add(new("🔗 Copy link", () => MouseActionService.RunAsync(MouseActionService.CopyAction, link)));
            if (AccountStore.GetById(lead.AccountId) is not { TorOnly: true })
                items.Add(new("🌐 Open link", () => OpenUnsubscribePage(link)));
        }
        items.AddRange([new("☑ Select all listed messages", SelectAllMessages), new("✕ Clear selection", MailUiState.ClearSelection)]);
        ContextMenuService.Open(this, $"{targets.Length} message(s)", items, x, y);
    }

    /// <summary>Re-resolves a captured primary message before reply/export/open, preserving its folder even after a refresh.</summary>
    /// <param name="key">Captured full identity.</param>
    /// <returns>The current cached summary, focused with that same identity.</returns>
    MailMessageSummary ResolveMenuMessage(MailMessageIdentity key)
    {
        var account = AccountStore.GetById(key.AccountId) ?? throw new InvalidOperationException("The account no longer exists.");
        var message = MessageStore.GetSummary(key.AccountId, key.FolderFullName, key.Uid)
            ?? throw new InvalidOperationException("The message is no longer available in that folder.");
        if (MailUiState.SelectedAccount?.Id != account.Id) MailUiState.SelectAccount(account, restoreFolder: true);
        MailUiState.FocusMessage(message, key);
        return message;
    }

    /// <summary>Blank workspace space offers list, compose and sync actions without selecting or targeting any mail.</summary>
    /// <param name="pointer">Pointer location.</param>
    void OpenWorkspaceMenu(MouseEventArgs pointer)
    {
        ContextMenuService.Open(this, "Mail", [
            new("☑ Select all listed messages", SelectAllMessages, Rows.Count > 0),
            new("☑ Toggle selection mode", () => SelectionMode = !SelectionMode),
            new("✏️ New message", () => MouseActionService.RunAsync(MouseActionService.ComposeAction, string.Empty)),
            new("🔄 Check mail", () => MouseActionService.RunAsync(MouseActionService.SyncAction, string.Empty)),
            new("⚙️ Settings", () => Navigation.NavigateTo(NavigationConstants.Settings.Link))
        ], pointer.ClientX, pointer.ClientY);
    }

    /// <summary>Account actions retain their clicked owner instead of using whichever account happens to be selected.</summary>
    /// <param name="account">Clicked account.</param>
    /// <param name="pointer">Menu location.</param>
    internal void OpenAccountMenu(MailAccountData account, MouseEventArgs pointer) =>
        ContextMenuService.Open(this, account.EmailAddress, MailMenuItems.ForAccount(account), pointer.ClientX, pointer.ClientY);

    /// <summary>Attachment commands reuse the reader's existing save paths.</summary>
    /// <param name="attachment">Clicked attachment.</param>
    /// <param name="pointer">Menu location.</param>
    internal void OpenAttachmentMenu(AttachmentInfo attachment, MouseEventArgs pointer) => ContextMenuService.Open(this, attachment.FileName, [
        new("💾 Save attachment", () => SaveAttachmentAsync(attachment.Index)),
        new("📦 Save all attachments", SaveAllAttachmentsAsync),
        new("⧉ Copy file name", () => MouseActionService.RunAsync(MouseActionService.CopyAction, attachment.FileName))
    ], pointer.ClientX, pointer.ClientY);

    /// <summary>Tags expose existing search and tag-removal operations for their exact message.</summary>
    /// <param name="tag">Tag name.</param>
    /// <param name="message">Tagged message; null means a person/profile tag.</param>
    /// <param name="pointer">Menu location.</param>
    internal void OpenTagMenu(string tag, MailMessageSummary? message, MouseEventArgs pointer)
    {
        List<ContextMenuItem> items = [new("🔎 Search this tag", () => SearchByTag(tag)),
            new("⧉ Copy tag", () => MouseActionService.RunAsync(MouseActionService.CopyAction, tag))];
        if (message != null)
        {
            var keys = MessageTargets(message);
            items.Add(new("Remove tag from this message", () => MouseActionService.RunMessagesAsync(MailMessageCommand.RemoveTag, keys, tag)));
        }
        ContextMenuService.Open(this, tag, items, pointer.ClientX, pointer.ClientY);
    }

    /// <summary>Address actions are shared by the sender and person sheet.</summary>
    /// <param name="address">Clicked address.</param>
    /// <param name="pointer">Menu location.</param>
    internal void OpenAddressMenu(string address, MouseEventArgs pointer) => ContextMenuService.Open(this, address, [
        new("⧉ Copy address", () => MouseActionService.RunAsync(MouseActionService.CopyAction, address)),
        new("✏️ Write to this address", () => MouseActionService.RunAsync(MouseActionService.ComposeToAction, address)),
        new("👤 Person details", () => MailUiState.OpenPerson(address)),
        new("🔎 Mail from this address", () => RunSavedSearch("from:" + address)),
        new("🔎 Mail to this address", () => RunSavedSearch("to:" + address))
    ], pointer.ClientX, pointer.ClientY);

    /// <summary>Exposes existing draft save, validation and discard behavior without another composing workflow.</summary>
    /// <param name="pointer">Menu location.</param>
    internal void OpenComposeMenu(MouseEventArgs pointer)
    {
        if (MailUiState.ActiveCompose is not { } draft) return;
        ContextMenuService.Open(this, "Draft", [
            new("💾 Save draft", SaveActiveDraft),
            new("📨 Send", QueueActiveDraftSend, !string.IsNullOrWhiteSpace(draft.To)),
            new("💾 Save and close", SaveAndCloseCompose),
            new("🗑️ Discard draft", DiscardCompose),
            new("⧉ Copy subject", () => MouseActionService.RunAsync(MouseActionService.CopyAction, draft.Subject)),
            new("⧉ Copy draft text", () => MouseActionService.RunAsync(MouseActionService.CopyAction, draft.Body)),
            new("⧉ Copy recipients", () => MouseActionService.RunAsync(MouseActionService.CopyAction, draft.To))
        ], pointer.ClientX, pointer.ClientY);
    }

    /// <summary>Shares attachment removal with its existing chip button and compose autosave.</summary>
    /// <param name="path">Captured staged attachment path.</param>
    /// <param name="pointer">Menu location.</param>
    internal void OpenComposeAttachmentMenu(string path, MouseEventArgs pointer) => ContextMenuService.Open(this, Path.GetFileName(path), [
        new("🗑️ Remove attachment", () => RemoveComposeAttachment(path)),
        new("⧉ Copy file name", () => MouseActionService.RunAsync(MouseActionService.CopyAction, Path.GetFileName(path)))
    ], pointer.ClientX, pointer.ClientY);

    /// <summary>Shares the saved-search chip's existing run/remove operations with its context menu.</summary>
    /// <param name="query">Captured saved query.</param>
    /// <param name="pointer">Menu location.</param>
    internal void OpenSavedSearchMenu(string query, MouseEventArgs pointer) => ContextMenuService.Open(this, "Saved search", [
        new("🔎 Run saved search", () => RunSavedSearch(query)),
        new("⧉ Copy query", () => MouseActionService.RunAsync(MouseActionService.CopyAction, query)),
        new("🗑️ Remove saved search", () => RemoveSavedSearch(query))
    ], pointer.ClientX, pointer.ClientY);
}
