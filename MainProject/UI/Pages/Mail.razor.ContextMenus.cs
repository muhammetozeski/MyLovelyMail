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

    /// <summary>
    /// Opens the message context menu without mutating Column 2 selection when right-clicking outside the selection
    /// (decoupling Column 3 reader right-clicks from Column 2 bulk selection).
    /// </summary>
    internal void OpenMessageMenu(IEnumerable<MailMessageIdentity> clicked, double x, double y,
        MouseMenuRequest? native = null, MailMessageIdentity? primary = null)
    {
        var clickedKeys = clicked.Distinct().ToArray();
        if (clickedKeys.Length == 0) return;

        // If the clicked message is already part of an active multi-selection, act on the selection;
        // otherwise act ONLY on clickedKeys without auto-selecting rows in Column 2.
        var targets = clickedKeys.Any(MailUiState.SelectedMessages.Contains)
            ? MailUiState.SelectedMessages.ToArray()
            : clickedKeys;

        var lead = primary ?? clickedKeys.OrderByDescending(key => MessageStore.GetSummary(key.AccountId, key.FolderFullName, key.Uid)?.DateUtc).First();
        var leadFolder = MessageStore.GetFolders(lead.AccountId).FirstOrDefault(f => f.FullName == lead.FolderFullName);
        bool isTrash = leadFolder?.Role == FolderRole.Trash;

        ContextMenuItem Command(string label, MailMessageCommand command, string? argument = null, DateTime? dueUtc = null, string? pinId = null) =>
            new(label, () => MouseActionService.RunMessagesAsync(command, targets, argument, dueUtc, includeSelection: false), PinActionId: pinId);

        List<ContextMenuItem> items =
        [
            Command("✅ Mark read · Mouse 5", MailMessageCommand.Read, pinId: "toggle-read"),
            Command("📩 Mark unread", MailMessageCommand.Unread, pinId: "toggle-read"),
            new("⭐ Star, Importance & Tags…", () =>
            {
                ModalService.Open<MessageMarkersModal>(new Dictionary<string, object?>
                {
                    [nameof(MessageMarkersModal.Targets)] = targets
                }, ModalSize.Compact);
            }, pinActionId: "markers"),
            Command(isTrash ? "🗑️ Delete forever · Mouse 4" : "🗑️ Move to Trash · Mouse 4", MailMessageCommand.Trash, pinId: "trash"),
            Command("📦 Archive", MailMessageCommand.Archive, pinId: "archive"),
            Command("🚫 Move to Spam", MailMessageCommand.Spam, pinId: "spam"),
            Command("📥 Restore to Inbox (local for local mail)", MailMessageCommand.Restore, pinId: "restore"),
            Command("⭐ Toggle stars", MailMessageCommand.ToggleStar, pinId: "star"),
            Command("❗ Toggle importance", MailMessageCommand.ToggleImportant, pinId: "important")
        ];

        var moves = MoveTargets(targets);
        if (moves.Count > 0)
            items.Add(new("📂 Move to", Children: [.. moves.Select(folder => Command(FolderLabel(folder), MailMessageCommand.Move, folder.FullName))], PinActionId: "move"));
        if (TagStore.AllTags.Count > 0)
        {
            items.Add(new("🏷️ Add tag", Children: [.. TagStore.AllTags.Select(tag => Command(tag, MailMessageCommand.AddTag, tag))]));
            items.Add(new("🏷️ Remove tag", Children: [.. TagStore.AllTags.Select(tag => Command(tag, MailMessageCommand.RemoveTag, tag))]));
        }
        items.Add(new("💤 Snooze", Children: [.. SnoozeService.Presets.Select(preset =>
            new ContextMenuItem(preset.Label, () => MouseActionService.RunMessagesAsync(MailMessageCommand.Snooze, targets, dueUtc: preset.DueUtc(), includeSelection: false)))], PinActionId: "snooze"));
        if (targets.Any(key => MessageStore.GetSummary(key.AccountId, key.FolderFullName, key.Uid)?.SnoozedUntilUtc != null))
            items.Add(Command("☀️ Wake now", MailMessageCommand.Wake));

        bool oneConversation = targets.Length == clickedKeys.Length && targets.All(clickedKeys.Contains);
        if (oneConversation)
        {
            items.AddRange([
                new("📖 Open message", () => OpenMessage(ResolveMenuMessage(lead))),
                new("↩️ Reply", () => StartReply(ResolveMenuMessage(lead), false), pinActionId: "reply"),
                new("↩️ Reply all", () => StartReply(ResolveMenuMessage(lead), true), pinActionId: "reply-all"),
                new("↪️ Forward", () => StartForward(ResolveMenuMessage(lead)), pinActionId: "forward"),
                new("📎 Forward as attachment", () => StartForwardAsAttachment(ResolveMenuMessage(lead)), pinActionId: "forward-attachment"),
                new("💾 Save message (.eml / .html)…", () => OpenSaveModal(ResolveMenuMessage(lead)), pinActionId: "save"),
                new("📄 Save message as .eml", () => ExportOpen(ResolveMenuMessage(lead), false), pinActionId: "save-eml"),
                new("🌐 Save message as .html", () => ExportOpen(ResolveMenuMessage(lead), true), pinActionId: "save-html"),
                new("🧾 View message source", () => OpenMessageSource(ResolveMenuMessage(lead)), pinActionId: "source"),
                new("📋 Copy subject", () => MouseActionService.RunAsync(MouseActionService.CopyAction, ResolveMenuMessage(lead).Subject)),
                new("📋 Copy sender address", () => MouseActionService.RunAsync(MouseActionService.CopyAction, ResolveMenuMessage(lead).FromAddress)),
                new("👤 Sender actions", Children: SenderMenuItems(lead.AccountId,
                    MessageStore.GetSummary(lead.AccountId, lead.FolderFullName, lead.Uid)?.FromAddress ?? string.Empty), PinActionId: "person-details")
            ]);
        }
        if (native?.Selection is { Length: > 0 } selected)
            items.Add(new("📋 Copy selected text", () => MouseActionService.RunAsync(MouseActionService.CopyAction, selected)));
        if (native?.Link is { Length: > 0 } link)
        {
            items.Add(new("🔗 Copy link", () => MouseActionService.RunAsync(MouseActionService.CopyAction, link)));
            if (AccountStore.GetById(lead.AccountId) is { TorOnly: false, Protocol: not IncomingProtocol.FastMail })
                items.Add(new("🌐 Open link", () => OpenUnsubscribePage(link)));
        }
        AddGeneralMessageMenuItems(items, targets, lead);
        items.AddRange([new("☑️ Select all listed messages", SelectAllMessages), new("✕ Clear selection", MailUiState.ClearSelection)]);
        ContextMenuService.Open(this, $"{targets.Length} message(s)", items, x, y);
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

    /// <summary>Blank workspace space offers list, compose and sync actions without selecting or targeting any mail.</summary>
    void OpenWorkspaceMenu(MouseEventArgs pointer)
    {
        ContextMenuService.Open(this, "Mail", [
            new("☑️ Select all listed messages", SelectAllMessages, Rows.Count > 0),
            new("☑ Toggle selection mode", () => SelectionMode = !SelectionMode),
            new("✏️ New message", () => MouseActionService.RunAsync(MouseActionService.ComposeAction, string.Empty)),
            new("🔄 Check mail", () => MouseActionService.RunAsync(MouseActionService.SyncAction, string.Empty)),
            new("⚙️ Settings", () => Navigation.NavigateTo(NavigationConstants.Settings.Link)),
            .. GeneralWorkspaceMenuItems()
        ], pointer.ClientX, pointer.ClientY);
    }

    internal void OpenAccountMenu(MailAccountData account, MouseEventArgs pointer) =>
        ContextMenuService.Open(this, account.EmailAddress, MailMenuItems.ForAccount(account, () => DeleteFastMailAccountAsync(account)), pointer.ClientX, pointer.ClientY);

    internal void OpenAccountFolderMenu(AccountFolderData folder, MouseEventArgs pointer) =>
        ContextMenuService.Open(this, folder.Name, MailMenuItems.ForAccountFolder(folder), pointer.ClientX, pointer.ClientY);

    internal void OpenAttachmentMenu(AttachmentInfo attachment, MouseEventArgs pointer) => ContextMenuService.Open(this, attachment.FileName, [
        new("💾 Save attachment", () => SaveAttachmentAsync(attachment.Index)),
        new("📦 Save all attachments", SaveAllAttachmentsAsync),
        new("📋 Copy file name", () => MouseActionService.RunAsync(MouseActionService.CopyAction, attachment.FileName))
    ], pointer.ClientX, pointer.ClientY);

    internal void OpenTagMenu(string tag, MailMessageSummary? message, MouseEventArgs pointer)
    {
        List<ContextMenuItem> items = [new("🔎 Search this tag", () => SearchByTag(tag)),
            new("📋 Copy tag", () => MouseActionService.RunAsync(MouseActionService.CopyAction, tag))];
        if (message != null)
        {
            var keys = MessageTargets(message);
            items.Add(new("Remove tag from this message", () => MouseActionService.RunMessagesAsync(MailMessageCommand.RemoveTag, keys, tag, includeSelection: false)));
        }
        ContextMenuService.Open(this, tag, items, pointer.ClientX, pointer.ClientY);
    }

    internal void OpenComposeMenu(MouseEventArgs pointer)
    {
        if (MailUiState.ActiveCompose is not { } draft) return;
        ContextMenuService.Open(this, "Draft", [
            new("💾 Save draft", SaveActiveDraft),
            new("📨 Send", QueueActiveDraftSend, !string.IsNullOrWhiteSpace(draft.To)),
            new("📥 Save and close", SaveAndCloseCompose),
            new("🗑️ Discard draft", DiscardCompose),
            new("📋 Copy subject", () => MouseActionService.RunAsync(MouseActionService.CopyAction, draft.Subject)),
            new("📋 Copy draft text", () => MouseActionService.RunAsync(MouseActionService.CopyAction, draft.Body)),
            new("📋 Copy recipients", () => MouseActionService.RunAsync(MouseActionService.CopyAction, draft.To))
        ], pointer.ClientX, pointer.ClientY);
    }

    internal void OpenComposeAttachmentMenu(string path, MouseEventArgs pointer) => ContextMenuService.Open(this, Path.GetFileName(path), [
        new("🗑️ Remove attachment", () => RemoveComposeAttachment(path)),
        new("📋 Copy file name", () => MouseActionService.RunAsync(MouseActionService.CopyAction, Path.GetFileName(path)))
    ], pointer.ClientX, pointer.ClientY);

    internal void OpenSavedSearchMenu(string query, MouseEventArgs pointer) => ContextMenuService.Open(this, "Saved search", [
        new("🔎 Run saved search", () => RunSavedSearch(query)),
        new("📋 Copy query", () => MouseActionService.RunAsync(MouseActionService.CopyAction, query)),
        new("🗑️ Remove saved search", () => RemoveSavedSearch(query))
    ], pointer.ClientX, pointer.ClientY);
}
