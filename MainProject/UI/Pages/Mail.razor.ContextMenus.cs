using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Web;
using MyLovelyMail.MainProject.Constants;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.UI.Components;
using MyLovelyMail.MainProject.UI.Elements.Modals;

namespace MyLovelyMail.MainProject.UI.Pages;

public partial class Mail
{
    /// <summary>Handles a reader-frame request on the owning renderer with its captured identities and selection.</summary>
    Task HandleNativeMailMenuAsync(MouseMenuRequest request) => InvokeAsync(() =>
    {
        var keys = JsonSerializer.Deserialize<MailMessageIdentity[]>(request.MessagePayload) ?? [];
        MailMessageIdentity? primary = request.PrimaryPayload.Length == 0 ? null
            : JsonSerializer.Deserialize<MailMessageIdentity>(request.PrimaryPayload);
        OpenMessageMenu(keys, request.X, request.Y, request, primary);
    });

    internal IReadOnlyList<ContextMenuItem> BuildMessageMenuItems(
        MailMessageIdentity[] targets,
        MailMessageIdentity lead,
        bool oneConversation = true,
        MouseMenuRequest? native = null)
    {
        var leadFolder = MessageStore.GetFolders(lead.AccountId).FirstOrDefault(f => f.FullName == lead.FolderFullName);
        bool isTrash = leadFolder?.Role == FolderRole.Trash;

        ContextMenuItem Command(string label, MailMessageCommand command, string actionId, string? argument = null, DateTime? dueUtc = null) =>
            new(label, () => MouseActionService.RunMessagesAsync(command, targets, argument, dueUtc, includeSelection: false), ActionId: actionId);

        List<ContextMenuItem> items =
        [
            Command("✅ Mark read · Mouse 5", MailMessageCommand.Read, "Msg.Read"),
            Command("📩 Mark unread", MailMessageCommand.Unread, "Msg.Unread"),
            new("⭐ Star, Importance & Tags…", () =>
            {
                ModalService.Open<MessageMarkersModal>(new Dictionary<string, object?>
                {
                    [nameof(MessageMarkersModal.Targets)] = targets
                }, ModalSize.Compact);
            }, actionId: "Msg.MarkersModal"),
            Command(isTrash ? "🗑️ Delete forever · Mouse 4" : "🗑️ Move to Trash · Mouse 4", MailMessageCommand.Trash, "Msg.Trash"),
            Command("📦 Archive", MailMessageCommand.Archive, "Msg.Archive"),
            Command("🚫 Move to Spam", MailMessageCommand.Spam, "Msg.Spam"),
            Command("📥 Restore to Inbox (local for local mail)", MailMessageCommand.Restore, "Msg.Restore"),
            Command("⭐ Toggle stars", MailMessageCommand.ToggleStar, "Msg.ToggleStar"),
            Command("❗ Toggle importance", MailMessageCommand.ToggleImportant, "Msg.ToggleImportant"),
            Command("🔇 Toggle mute", MailMessageCommand.ToggleMute, "Msg.ToggleMute")
        ];

        var moves = MoveTargets(targets);
        if (moves.Count > 0)
        {
            items.Add(new("📂 Move to",
                Children: [.. moves.Select(folder => Command(FolderLabel(folder), MailMessageCommand.Move, $"Msg.Move:{folder.FullName}", folder.FullName))],
                ActionId: "Msg.MoveGroup"));
        }

        if (TagStore.AllTags.Count > 0)
        {
            items.Add(new("🏷️ Add tag",
                Children: [.. TagStore.AllTags.Select(tag => Command($"🏷️ {tag}", MailMessageCommand.AddTag, $"Msg.AddTag:{tag}", tag))],
                ActionId: "Msg.AddTagGroup"));
            items.Add(new("🏷️ Remove tag",
                Children: [.. TagStore.AllTags.Select(tag => Command($"✕ {tag}", MailMessageCommand.RemoveTag, $"Msg.RemoveTag:{tag}", tag))],
                ActionId: "Msg.RemoveTagGroup"));
        }

        items.Add(new("💤 Snooze",
            Children: [.. SnoozeService.Presets.Select(preset =>
                new ContextMenuItem(preset.Label, () => MouseActionService.RunMessagesAsync(MailMessageCommand.Snooze, targets, dueUtc: preset.DueUtc(), includeSelection: false), ActionId: $"Msg.Snooze:{preset.Label}"))],
            ActionId: "Msg.SnoozeGroup"));

        if (targets.Any(key => MessageStore.GetSummary(key.AccountId, key.FolderFullName, key.Uid)?.SnoozedUntilUtc != null))
            items.Add(Command("☀️ Wake now", MailMessageCommand.Wake, "Msg.Wake"));

        if (oneConversation)
        {
            string senderAddr = MessageStore.GetSummary(lead.AccountId, lead.FolderFullName, lead.Uid)?.FromAddress ?? string.Empty;
            items.AddRange([
                new("📖 Open message", () => OpenMessage(ResolveMenuMessage(lead)), actionId: "Msg.Open"),
                new("↩️ Reply", () => StartReply(ResolveMenuMessage(lead), false), actionId: "Msg.Reply"),
                new("↩️ Reply all", () => StartReply(ResolveMenuMessage(lead), true), actionId: "Msg.ReplyAll"),
                new("↪️ Forward", () => StartForward(ResolveMenuMessage(lead)), actionId: "Msg.Forward"),
                new("📎 Forward as attachment", () => StartForwardAsAttachment(ResolveMenuMessage(lead)), actionId: "Msg.ForwardAttachment"),
                new("💾 Save message (.eml / .html)…", () => OpenSaveModal(ResolveMenuMessage(lead)), actionId: "Msg.SaveModal"),
                new("📄 Save message as .eml", () => ExportOpen(ResolveMenuMessage(lead), false), ActionId: "Msg.SaveEml"),
                new("🌐 Save message as .html", () => ExportOpen(ResolveMenuMessage(lead), true), ActionId: "Msg.SaveHtml"),
                new("🧾 View message source", () => OpenMessageSource(ResolveMenuMessage(lead)), actionId: "Msg.ViewSource"),
                new("📋 Copy subject", () => MouseActionService.RunAsync(MouseActionService.CopyAction, ResolveMenuMessage(lead).Subject), ActionId: "Msg.CopySubject"),
                new("📋 Copy sender address", () => MouseActionService.RunAsync(MouseActionService.CopyAction, ResolveMenuMessage(lead).FromAddress), ActionId: "Msg.CopySender"),
                new("👤 Sender actions", Children: SenderMenuItems(lead.AccountId, senderAddr), ActionId: "Msg.SenderMenu")
            ]);
        }
        if (native?.Selection is { Length: > 0 } selected)
            items.Add(new("📋 Copy selected text", () => MouseActionService.RunAsync(MouseActionService.CopyAction, selected), ActionId: "Msg.CopySelectedText"));
        if (native?.Link is { Length: > 0 } link)
        {
            items.Add(new("🔗 Copy link", () => MouseActionService.RunAsync(MouseActionService.CopyAction, link), ActionId: "Msg.CopyLink"));
            if (AccountStore.GetById(lead.AccountId) is { TorOnly: false, Protocol: not IncomingProtocol.FastMail })
                items.Add(new("🌐 Open link", () => OpenUnsubscribePage(link), actionId: "Msg.OpenLink"));
        }
        AddGeneralMessageMenuItems(items, targets, lead);
        items.AddRange([
            new("☑️ Select all listed messages", SelectAllMessages, actionId: "List.SelectAll"),
            new("✕ Clear selection", MailUiState.ClearSelection, actionId: "List.ClearSelection")
        ]);
        return items;
    }

    /// <summary>
    /// Opens the message context menu without mutating Column 2 selection when right-clicking outside the selection
    /// (decoupling Column 3 reader right-clicks from Column 2 bulk selection).
    /// </summary>
    internal void OpenMessageMenu(IEnumerable<MailMessageIdentity> clicked, double x, double y,
        MouseMenuRequest? native = null, MailMessageIdentity? primary = null)
    {
        var clickedKeys = clicked.Distinct().ToArray();
        if (clickedKeys.Length == 0) return;

        var targets = clickedKeys.Any(MailUiState.SelectedMessages.Contains)
            ? MailUiState.SelectedMessages.ToArray()
            : clickedKeys;

        var lead = primary ?? clickedKeys.OrderByDescending(key => MessageStore.GetSummary(key.AccountId, key.FolderFullName, key.Uid)?.DateUtc).First();
        bool oneConversation = targets.Length == clickedKeys.Length && targets.All(clickedKeys.Contains);

        var items = BuildMessageMenuItems(targets, lead, oneConversation, native);
        ContextMenuService.Open(
            this,
            $"{targets.Length} message(s)",
            items,
            x,
            y,
            UiSurfaceIds.MessageContextMenu,
            id => ResolveMenuAddedItem(UiSurfaceIds.MessageContextMenu, id, messageTargets: targets, leadMessage: lead));
    }

    /// <summary>Re-resolves a captured primary message before reply/export/open, preserving its folder even after a refresh.</summary>
    internal MailMessageSummary ResolveMenuMessage(MailMessageIdentity key)
    {
        var account = AccountStore.GetById(key.AccountId) ?? throw new InvalidOperationException("The account no longer exists.");
        var message = MessageStore.GetSummary(key.AccountId, key.FolderFullName, key.Uid)
            ?? throw new InvalidOperationException("The message is no longer available in that folder.");
        if (MailUiState.SelectedAccount?.Id != account.Id) MailUiState.SelectAccount(account, restoreFolder: true);
        MailUiState.FocusMessage(message, key);
        return message;
    }

    internal IReadOnlyList<ContextMenuItem> BuildWorkspaceMenuItems() =>
    [
        new("☑️ Select all listed messages", SelectAllMessages, Rows.Count > 0, actionId: "List.SelectAll"),
        new("☑ Toggle selection mode", () => SelectionMode = !SelectionMode, actionId: "List.SelectMode"),
        new("✏️ New message", () => MouseActionService.RunAsync(MouseActionService.ComposeAction, string.Empty), ActionId: "Workspace.Compose"),
        new("🔄 Check mail", () => MouseActionService.RunAsync(MouseActionService.SyncAction, string.Empty), ActionId: "Workspace.SyncAll"),
        new("⚙️ Settings", () => Navigation.NavigateTo(NavigationConstants.Settings.Link), actionId: "Workspace.Settings"),
        .. GeneralWorkspaceMenuItems()
    ];

    /// <summary>Blank workspace space offers list, compose and sync actions without selecting or targeting any mail.</summary>
    void OpenWorkspaceMenu(MouseEventArgs pointer)
    {
        ContextMenuService.Open(
            this,
            "Mail",
            BuildWorkspaceMenuItems(),
            pointer.ClientX,
            pointer.ClientY,
            UiSurfaceIds.WorkspaceContextMenu,
            id => ResolveMenuAddedItem(UiSurfaceIds.WorkspaceContextMenu, id));
    }

    internal void OpenAccountMenu(MailAccountData account, MouseEventArgs pointer) =>
        ContextMenuService.Open(
            this,
            account.EmailAddress,
            MailMenuItems.ForAccount(account, () => DeleteFastMailAccountAsync(account)),
            pointer.ClientX,
            pointer.ClientY,
            UiSurfaceIds.AccountContextMenu,
            id => ResolveMenuAddedItem(UiSurfaceIds.AccountContextMenu, id, accountContext: account));

    internal void OpenAccountFolderMenu(AccountFolderData folder, MouseEventArgs pointer) =>
        ContextMenuService.Open(
            this,
            folder.Name,
            MailMenuItems.ForAccountFolder(folder),
            pointer.ClientX,
            pointer.ClientY,
            UiSurfaceIds.AccountFolderContextMenu,
            id => ResolveMenuAddedItem(UiSurfaceIds.AccountFolderContextMenu, id, accountFolderContext: folder));

    internal void OpenAttachmentMenu(AttachmentInfo attachment, MouseEventArgs pointer) => ContextMenuService.Open(this, attachment.FileName, [
        new("💾 Save attachment", () => SaveAttachmentAsync(attachment.Index), ActionId: "Attachment.Save"),
        new("📦 Save all attachments", SaveAllAttachmentsAsync, ActionId: "Attachment.SaveAll"),
        new("📋 Copy file name", () => MouseActionService.RunAsync(MouseActionService.CopyAction, attachment.FileName), ActionId: "Attachment.CopyName")
    ], pointer.ClientX, pointer.ClientY);

    internal void OpenTagMenu(string tag, MailMessageSummary? message, MouseEventArgs pointer)
    {
        List<ContextMenuItem> items = [
            new("🔎 Search this tag", () => SearchByTag(tag), actionId: $"Tag.Search:{tag}"),
            new("📋 Copy tag", () => MouseActionService.RunAsync(MouseActionService.CopyAction, tag), ActionId: $"Tag.Copy:{tag}")
        ];
        if (message != null)
        {
            var keys = MessageTargets(message);
            items.Add(new("Remove tag from this message", () => MouseActionService.RunMessagesAsync(MailMessageCommand.RemoveTag, keys, tag, includeSelection: false), ActionId: $"Msg.RemoveTag:{tag}"));
        }
        ContextMenuService.Open(this, tag, items, pointer.ClientX, pointer.ClientY);
    }

    internal void OpenComposeMenu(MouseEventArgs pointer)
    {
        if (MailUiState.ActiveCompose is not { } draft) return;
        ContextMenuService.Open(this, "Draft", [
            new("💾 Save draft", SaveActiveDraft, actionId: "Compose.SaveDraft"),
            new("📨 Send", QueueActiveDraftSend, !string.IsNullOrWhiteSpace(draft.To), actionId: "Compose.Send"),
            new("📥 Save and close", SaveAndCloseCompose, actionId: "Compose.SaveClose"),
            new("🗑️ Discard draft", DiscardCompose, actionId: "Compose.Discard"),
            new("📋 Copy subject", () => MouseActionService.RunAsync(MouseActionService.CopyAction, draft.Subject), ActionId: "Compose.CopySubject"),
            new("📋 Copy draft text", () => MouseActionService.RunAsync(MouseActionService.CopyAction, draft.Body), ActionId: "Compose.CopyBody"),
            new("📋 Copy recipients", () => MouseActionService.RunAsync(MouseActionService.CopyAction, draft.To), ActionId: "Compose.CopyTo")
        ], pointer.ClientX, pointer.ClientY);
    }

    internal void OpenComposeAttachmentMenu(string path, MouseEventArgs pointer) => ContextMenuService.Open(this, Path.GetFileName(path), [
        new("🗑️ Remove attachment", () => RemoveComposeAttachment(path), actionId: "ComposeAttachment.Remove"),
        new("📋 Copy file name", () => MouseActionService.RunAsync(MouseActionService.CopyAction, Path.GetFileName(path)), ActionId: "ComposeAttachment.CopyName")
    ], pointer.ClientX, pointer.ClientY);

    internal void OpenSavedSearchMenu(string query, MouseEventArgs pointer) => ContextMenuService.Open(this, "Saved search", [
        new("🔎 Run saved search", () => RunSavedSearch(query), actionId: "SavedSearch.Run"),
        new("📋 Copy query", () => MouseActionService.RunAsync(MouseActionService.CopyAction, query), ActionId: "SavedSearch.Copy"),
        new("🗑️ Remove saved search", () => RemoveSavedSearch(query), actionId: "SavedSearch.Remove")
    ], pointer.ClientX, pointer.ClientY);
}
