using Microsoft.AspNetCore.Components.Web;
using MyLovelyMail.MainProject.Constants;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.UI.Components;
using MyLovelyMail.MainProject.UI.Elements.Buttons;
using MyLovelyMail.MainProject.UI.Elements.Modals;
using GlobalSettings = MyLovelyMail.MainProject.Stores.Settings;

namespace MyLovelyMail.MainProject.UI.Pages;

public partial class Mail
{
    static ActionVisualKind DefaultVisualKindForSurface(string surfaceId) => surfaceId switch
    {
        UiSurfaceIds.MailRowHover or UiSurfaceIds.SenderRowHover => ActionVisualKind.RowQuick,
        UiSurfaceIds.SidebarFolderHover or UiSurfaceIds.SidebarAccountFolderHover => ActionVisualKind.SidebarQuick,
        _ => ActionVisualKind.Pill
    };

    MailMessageIdentity[] ResolveActiveMessageTargets(MailMessageIdentity[]? explicitTargets = null)
    {
        if (explicitTargets is { Length: > 0 }) return explicitTargets;
        if (MailUiState.SelectedMessages.Count > 0) return [.. MailUiState.SelectedMessages];
        if (MailUiState.OpenMessage is { } open) return MessageTargets(open);
        if (MailUiState.FocusedMessage is { } focused) return MessageTargets(focused);
        return [];
    }

    MailMessageSummary? ResolvePrimaryMessage(MailMessageSummary? explicitMessage = null, MailMessageIdentity? explicitLead = null)
    {
        if (explicitMessage != null) return explicitMessage;
        if (explicitLead is { } lead && MessageStore.GetSummary(lead.AccountId, lead.FolderFullName, lead.Uid) is { } found)
            return found;
        return MailUiState.OpenMessage ?? MailUiState.FocusedMessage;
    }

    (string? AccountId, string? Address) ResolveActiveSenderContext(string? explicitAccountId = null, string? explicitAddress = null)
    {
        string? accId = explicitAccountId ?? MailUiState.SelectedAccount?.Id;
        if (!string.IsNullOrWhiteSpace(explicitAddress))
            return (accId, explicitAddress.Trim());
        if (!string.IsNullOrWhiteSpace(MailUiState.OpenPersonAddress))
            return (accId, MailUiState.OpenPersonAddress.Trim());
        if (MailUiState.OpenMessage is { FromAddress.Length: > 0 } open)
            return (MessageIdentityOf(open)?.AccountId ?? accId, open.FromAddress.Trim());
        if (MailUiState.FocusedMessage is { FromAddress.Length: > 0 } focused)
            return (MessageIdentityOf(focused)?.AccountId ?? accId, focused.FromAddress.Trim());
        return (accId, null);
    }

    // ---- Default Toolbar Action Builders ----

    internal IReadOnlyList<UiActionModel> BuildListToolbarActions()
    {
        List<UiActionModel> list =
        [
            UiActionModel.CreateSync("List.SelectMode", "☑ Select", () => SelectionMode = !SelectionMode,
                title: "Toggle multi-selection mode", isActive: SelectionMode),
            UiActionModel.CreateSync("List.SelectAll", "Select all", SelectAllMessages,
                title: "Select all listed messages (Ctrl+A)", disabled: Rows.Count == 0),
            UiActionModel.CreateMouseSync("List.SelectMenu", "Select ▾", OpenSelectionMenu,
                title: "Select by read state, stars, attachments or tags", disabled: Rows.Count == 0),
            UiActionModel.CreateMouseSync("List.SortMenu", $"⇅ {(IsSenderListView ? ListViewState.SenderSortLabel : ListViewState.MailSortLabel)} ▾", OpenSortMenu,
                title: "Sort list"),
            UiActionModel.CreateSync("List.SenderView", IsSenderListView ? "✉️ Mails" : "👥 Senders", ToggleSenderListView,
                title: "Switch between Mail List and Sender List", isActive: IsSenderListView),
            UiActionModel.CreateSync("List.ToggleQuickFilters",
                $"🎛️ Filters{(!ShowSearchTools && ActiveFilterCount > 0 ? $" · {ActiveFilterCount}" : "")} {(ShowSearchTools ? "▴" : "▾")}",
                () => ShowSearchTools = !ShowSearchTools,
                title: ShowSearchTools ? "Hide quick filter pills" : "Show quick filter pills",
                isActive: !ShowSearchTools && ActiveFilterCount > 0),
            UiActionModel.CreateSync("List.ToggleAdvancedFilters",
                $"🎯 Advanced{(ListViewState.ActiveFilterCount > 0 ? $" · {ListViewState.ActiveFilterCount}" : "")} {(ListViewState.IsFilterWindowOpen ? "▴" : "▾")}",
                ToggleAdvancedFilterWindow,
                title: "Open advanced draggable filters window",
                isActive: ListViewState.IsFilterWindowOpen),
            UiActionModel.CreateSync("List.Shortcuts", "⌨️", OpenShortcutHelpModal,
                title: "Keyboard shortcuts (?)", isIcon: true)
        ];

        if (MailUiState.SelectedFolder is { Role: FolderRole.Trash or FolderRole.Junk } trashOrJunkFolder
            && MailUiState.SelectedAccount is { } activeAcc
            && CachedCount > 0)
        {
            list.Add(new UiActionModel(
                "List.EmptyTrashOrSpam",
                trashOrJunkFolder.Role == FolderRole.Trash ? "🗑️ Empty Trash" : "🧹 Empty Spam",
                _ => EmptyFolderWithConfirmationAsync(activeAcc, trashOrJunkFolder),
                Title: $"Permanently delete all messages in {trashOrJunkFolder.DisplayName}",
                IsDanger: true));
        }

        if (MailUiState.SelectedMessages.Count > 0)
        {
            list.Add(UiActionModel.CreateSync("List.ClearSelection", "Clear selection", MailUiState.ClearSelection,
                title: "Clear active selection"));
        }

        return list;
    }

    internal IReadOnlyList<UiActionModel> BuildQuickFilterActions()
    {
        List<UiActionModel> list = [];
        foreach (var (label, token) in QuickFilters)
        {
            string capturedToken = token;
            list.Add(UiActionModel.CreateSync(
                $"Filter.Token:{capturedToken}",
                label,
                () => ToggleSearchToken(capturedToken),
                title: $"Filter by {capturedToken}",
                isActive: HasSearchToken(capturedToken)));
        }

        if (MailUiState.SelectedAccount != null)
        {
            list.Add(UiActionModel.CreateSync(
                "Filter.Conversations",
                "🧵 Conversations",
                ToggleConversationMode,
                title: "Group replies into conversations",
                isActive: ConversationMode));
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            list.Add(UiActionModel.CreateSync("Filter.AllFolders", "🗂️ All folders", ToggleSearchAllFolders,
                title: "Search across all folders in this account", isActive: SearchAllFolders));
            list.Add(UiActionModel.CreateSync("Filter.SaveSearch", "⭐ Save search", SaveCurrentSearch,
                title: "Pin this search query to the sidebar"));
        }

        return list;
    }

    internal IReadOnlyList<UiActionModel> BuildBulkToolbarActions()
    {
        bool bulkInTrash = MailUiState.SelectedFolder?.Role == FolderRole.Trash;
        List<UiActionModel> list =
        [
            UiActionModel.CreateMouseSync("Msg.MoreActions", "More actions ▾", OpenBulkActionsMenu, title: "All actions for selected messages"),
            new("Msg.Read", "✅ Read", _ => RunSelectionCommandAsync(MailMessageCommand.Read), Title: "Mark selected read"),
            new("Msg.Unread", "📩 Unread", _ => RunSelectionCommandAsync(MailMessageCommand.Unread), Title: "Mark selected unread"),
            UiActionModel.CreateSync("Msg.MarkersModal", "⭐ Labels", OpenBulkMarkersModal, title: "Star, Importance & Tags"),
        ];

        if (MoveTargets(MailUiState.SelectedMessages).Count > 0)
            list.Add(UiActionModel.CreateSync("Msg.MoveModal", "📂 Move", OpenBulkMoveModal, title: "Move selected to folder"));

        list.Add(UiActionModel.CreateSync("Msg.SnoozeModal", "⏰ Snooze", OpenBulkSnoozeModal, title: "Snooze selected messages"));
        list.Add(new("Msg.Trash", bulkInTrash ? "🗑️ Delete forever" : "🗑️ Trash",
            _ => RunSelectionCommandAsync(MailMessageCommand.Trash),
            Title: bulkInTrash ? "Permanently delete selected" : "Move selected to Trash",
            IsDanger: true));
        list.Add(UiActionModel.CreateSync("List.SelectAll", "☑️ All", SelectAllMessages, title: "Select all listed"));
        list.Add(UiActionModel.CreateSync("List.ClearSelection", "✕ Clear", MailUiState.ClearSelection, title: "Clear selection", isFixed: true));
        return list;
    }

    internal IReadOnlyList<UiActionModel> BuildMailRowHoverActions(MailMessageSummary message, MailMessageIdentity[] targets)
    {
        bool inTrash = MailUiState.SelectedFolder?.Role == FolderRole.Trash;
        bool unread = message.IsUnread;
        return
        [
            new("Msg.ToggleRead", unread ? "✓" : "📩",
                _ => MouseActionService.RunMessagesAsync(unread ? MailMessageCommand.Read : MailMessageCommand.Unread, targets, includeSelection: false),
                Title: unread ? "Mark read (Mouse 5)" : "Mark unread",
                VisualKind: ActionVisualKind.RowQuick),
            new("Msg.Trash", "🗑️",
                _ => MouseActionService.RunMessagesAsync(MailMessageCommand.Trash, targets, includeSelection: false),
                Title: inTrash ? "Delete forever (Mouse 4)" : "Move to Trash (Mouse 4)",
                IsDanger: true,
                VisualKind: ActionVisualKind.RowQuick),
            UiActionModel.CreateMouseSync("Msg.MoreActions", "⋯",
                e => OpenMessageMenu(targets, e.ClientX, e.ClientY, primary: MessageIdentityOf(message)),
                title: "Message actions",
                isFixed: true,
                visualKind: ActionVisualKind.RowQuick)
        ];
    }

    internal IReadOnlyList<UiActionModel> BuildSenderRowHoverActions(SenderItem sender)
    {
        string accountId = MailUiState.SelectedAccount?.Id ?? string.Empty;
        return
        [
            UiActionModel.CreateSync("Sender.OpenFolderMail", "✉️", () => SelectSender(sender),
                title: $"Show {sender.MessageCount} message(s) from {sender.DisplayName}",
                visualKind: ActionVisualKind.RowQuick),
            UiActionModel.CreateSync("Sender.Write", "✏️", () =>
                {
                    if (!string.IsNullOrEmpty(accountId)) ComposeToSender(accountId, sender.Address);
                },
                title: $"Write to {sender.Address}",
                visualKind: ActionVisualKind.RowQuick),
            new("Sender.MarkAllRead", "✓",
                _ => string.IsNullOrEmpty(accountId) ? Task.CompletedTask : RunSenderCommandAsync(accountId, sender.Address, MailMessageCommand.Read),
                Title: "Mark all cached mail from this sender read",
                Disabled: sender.UnreadCount == 0,
                VisualKind: ActionVisualKind.RowQuick),
            UiActionModel.CreateMouseSync("Sender.Menu", "⋯", e => OpenSenderRowMenu(sender, e),
                title: "Sender actions",
                isFixed: true,
                visualKind: ActionVisualKind.RowQuick)
        ];
    }

    internal IReadOnlyList<UiActionModel> BuildReaderHeaderNavActions() =>
    [
        UiActionModel.CreateSync("Nav.Prev", "←", () => OpenListedMessage(-1),
            title: "Previous listed message", isCompact: true, disabled: ListedNavigationTarget(-1) == null),
        UiActionModel.CreateSync("Nav.Next", "→", () => OpenListedMessage(1),
            title: "Next listed message", isCompact: true, disabled: ListedNavigationTarget(1) == null),
        UiActionModel.CreateSync("Nav.NextUnread", "📩→", () => OpenListedMessage(1, unreadOnly: true),
            title: "Next unread conversation", isCompact: true, disabled: ListedNavigationTarget(1, unreadOnly: true) == null)
    ];

    internal IReadOnlyList<UiActionModel> BuildReaderToolbarActions(MailMessageSummary open, MailMessageIdentity[] readerTargets)
    {
        bool isInTrash = ResolveFolderRole(open) == FolderRole.Trash;
        string starIcon = open.Flags.HasFlag(MailFlags.Flagged) ? "⭐" : "☆";
        string impIcon = open.Flags.HasFlag(MailFlags.Important) ? "❗" : "";
        string tagCountBadge = open.Tags.Count > 0 ? $" · {open.Tags.Count}" : "";

        return
        [
            new("Msg.ToggleRead", open.IsUnread ? "✅ Mark read" : "📩 Mark unread",
                _ => MouseActionService.RunMessagesAsync(open.IsUnread ? MailMessageCommand.Read : MailMessageCommand.Unread, readerTargets, includeSelection: false),
                Title: open.IsUnread ? "Mark read" : "Mark unread"),
            UiActionModel.CreateMouseSync("Msg.ReplyMenu", "↩️ Reply ▾", e => OpenReplyMenu(open, e),
                title: "Reply, Reply all, Forward, Forward as .eml"),
            UiActionModel.CreateSync("Msg.MarkersModal", $"{starIcon}{impIcon} Labels{tagCountBadge}", () => OpenMarkersModal(open),
                title: "Star, Importance & Tags"),
            UiActionModel.CreateSync("Msg.SaveModal", "💾 Save", () => OpenSaveModal(open),
                title: "Save message as .eml or .html"),
            UiActionModel.CreateMouseSync("Msg.MoreActions", "⚡ Message actions ▾", e => OpenReaderActionsMenu(open, e),
                title: "All message actions"),
            new("Msg.Archive", "📦 Archive",
                _ => MouseActionService.RunMessagesAsync(MailMessageCommand.Archive, readerTargets, includeSelection: false),
                Title: "Archive message"),
            UiActionModel.CreateSync("Msg.MoveModal", "📂 Move", () => OpenMoveModal(open),
                title: "Move message to folder"),
            UiActionModel.CreateSync("Msg.SnoozeModal", "⏰ Snooze", () => OpenSnoozeModal(open),
                title: "Snooze message until…"),
            UiActionModel.CreateMouseSync("Msg.SenderMenu", "👤 Sender ▾",
                e => OpenAddressMenu(open.FromAddress, e, MessageIdentityOf(open)?.AccountId),
                title: "Actions & rules for this sender"),
            new("Msg.Trash", isInTrash ? "🗑️ Delete forever" : "🗑️ Trash",
                _ => MouseActionService.RunMessagesAsync(MailMessageCommand.Trash, readerTargets, includeSelection: false),
                Title: isInTrash ? "Permanently delete message" : "Move message to Trash",
                IsDanger: true),
            UiActionModel.CreateSync("Reader.ScaleDown", "A-", () => StepReaderTextScale(-1),
                title: "Smaller text (Ctrl+-)", isCompact: true),
            UiActionModel.CreateSync("Reader.ScaleReset", $"{GlobalSettings.ReaderTextScalePercent.Value}%", () => StepReaderTextScale(0),
                title: "Reset text scale to 100% (Ctrl+0)", isCompact: true),
            UiActionModel.CreateSync("Reader.ScaleUp", "A+", () => StepReaderTextScale(1),
                title: "Larger text (Ctrl+=)", isCompact: true)
        ];
    }

    internal IReadOnlyList<UiActionModel> BuildPersonActions(PersonProfile person) =>
    [
        UiActionModel.CreateMouseSync("Sender.Menu", "Actions ▾", e => OpenAddressMenu(person.Address, e),
            title: "All sender commands & rules"),
        UiActionModel.CreateSync("Sender.FindFrom", "🔍 From them", () => RunSavedSearch("from:" + person.Address),
            title: $"Find mail from {person.Address}"),
        UiActionModel.CreateSync("Sender.FindTo", "🔍 To them", () => RunSavedSearch("to:" + person.Address),
            title: $"Find mail sent to {person.Address}"),
        new("Sender.Write", "✏️ Write", _ => MouseActionService.RunAsync(MouseActionService.ComposeToAction, person.Address),
            Title: $"Write new mail to {person.Address}")
    ];

    internal IReadOnlyList<UiActionModel> BuildComposeQuoteActions(ComposeDraft draft) =>
    [
        UiActionModel.CreateSync("Compose.QuoteFull", "Full", () => RequoteDraft(draft, QuoteStyle.Full),
            title: "Include full quoted message", isCompact: true),
        UiActionModel.CreateSync("Compose.QuoteTrimmed", "Trimmed", () => RequoteDraft(draft, QuoteStyle.Trimmed),
            title: "Include trimmed quote", isCompact: true),
        UiActionModel.CreateSync("Compose.QuoteNone", "None", () => RequoteDraft(draft, QuoteStyle.None),
            title: "Remove quoted text", isCompact: true)
    ];

    internal IReadOnlyList<UiActionModel> BuildComposeFooterActions(ComposeDraft draft) =>
    [
        UiActionModel.CreateSync("Compose.SaveDraft", "💾 Save draft", SaveActiveDraft, title: "Save draft now"),
        UiActionModel.CreateSync("Compose.Discard", "🗑️ Discard", DiscardCompose, title: "Discard this draft", isDanger: true),
        UiActionModel.CreateSync("Compose.Send", "📨 Send", QueueActiveDraftSend,
            title: "Send message", isActive: true, disabled: string.IsNullOrWhiteSpace(draft.To), isFixed: true)
    ];

    internal IReadOnlyList<UiActionModel> BuildSidebarTopActions()
    {
        List<UiActionModel> list =
        [
            UiActionModel.CreateSync("Sidebar.NewAccountFolder", "📁+ Group", () => ModalService.OpenCreateFolder(),
                title: "New account folder"),
        ];
        if (MailUiState.SelectedAccount != null)
        {
            list.Add(new("Workspace.Compose", "✏️ Compose",
                _ => MouseActionService.RunAsync(MouseActionService.ComposeAction, string.Empty),
                Title: "Write a new message",
                IsActive: true));
        }
        list.Add(new("Workspace.SyncAll", "🔄 Sync",
            _ => MouseActionService.RunAsync(MouseActionService.SyncAction, string.Empty),
            Title: "Check all enabled accounts now"));
        return list;
    }

    internal IReadOnlyList<UiActionModel> BuildSidebarFolderHoverActions(MailAccountData account, MailFolderData folder)
    {
        List<UiActionModel> list = [];
        if (folder.UnreadCount > 0 && folder.Role != FolderRole.Trash)
        {
            list.Add(new("Folder.ReadAll", "✓✓",
                _ => MouseActionService.ReadFolderAsync(folder),
                Title: $"Mark all {folder.UnreadCount} as read",
                VisualKind: ActionVisualKind.SidebarQuick));
        }
        list.Add(UiActionModel.CreateSync("Folder.Export", "📦", () => ExportFolderMailbox(folder),
            title: "Export this folder as one .mbox file",
            visualKind: ActionVisualKind.SidebarQuick));
        if (!folder.IsLocal && account.Protocol != IncomingProtocol.FastMail)
        {
            list.Add(UiActionModel.CreateSync("Folder.Resync", "🔄", () => ResyncFolder(folder),
                title: "Redownload this folder",
                visualKind: ActionVisualKind.SidebarQuick));
        }
        list.Add(UiActionModel.CreateMouseSync("Folder.Menu", "⋯", e => OpenFolderMenu(folder, e),
            title: "Folder actions",
            isFixed: true,
            visualKind: ActionVisualKind.SidebarQuick));
        return list;
    }

    internal IReadOnlyList<UiActionModel> BuildSidebarAccountFolderHoverActions(
        AccountFolderData accFolder,
        Func<Task> onCreateQuickMail,
        bool isPending,
        string? quickError) =>
    [
        UiActionModel.CreateSync("AccGroup.AddAccount", "➕", () => ModalService.OpenAddAccount(accFolder.Id),
            title: $"Add account to {accFolder.Name}",
            visualKind: ActionVisualKind.SidebarQuick),
        new("AccGroup.QuickMailTm", isPending ? "⏳" : "⚡+", _ => onCreateQuickMail(),
            Title: quickError ?? "Create a Mail.tm account here",
            Disabled: isPending,
            VisualKind: ActionVisualKind.SidebarQuick),
        UiActionModel.CreateMouseSync("AccGroup.Menu", "⋯", e => OpenAccountFolderMenu(accFolder, e),
            title: "Folder options",
            isFixed: true,
            visualKind: ActionVisualKind.SidebarQuick)
    ];

    // ---- Universal Cross-Surface Resolvers (Menu <-> Toolbar & Submenu -> Root) ----

    internal UiActionModel? ResolveToolbarAddedAction(
        string surfaceId,
        string actionId,
        MailMessageSummary? messageContext = null,
        MailMessageIdentity[]? targetsContext = null,
        SenderItem? senderRowContext = null,
        string? senderAddressContext = null,
        MailFolderData? folderContext = null,
        AccountFolderData? accountFolderContext = null)
    {
        var visual = DefaultVisualKindForSurface(surfaceId);
        bool compactIconBar = visual is ActionVisualKind.RowQuick or ActionVisualKind.SidebarQuick;
        var targets = ResolveActiveMessageTargets(targetsContext ?? (messageContext != null ? MessageTargets(messageContext) : null));
        var primaryMsg = ResolvePrimaryMessage(messageContext, targets.FirstOrDefault());
        var (senderAccId, senderAddr) = ResolveActiveSenderContext(
            folderContext?.AccountId,
            senderAddressContext ?? senderRowContext?.Address ?? messageContext?.FromAddress);

        UiActionModel Make(string label, string shortLabel, Func<MouseEventArgs, Task> run, bool disabled = false, bool isDanger = false, bool isActive = false, string? title = null) =>
            new(actionId, compactIconBar ? shortLabel : label, run, Title: title ?? label, IsActive: isActive, IsDanger: isDanger, IsPinned: visual == ActionVisualKind.Pill, Disabled: disabled, VisualKind: visual);

        if (actionId.StartsWith("Msg.AddTag:", StringComparison.OrdinalIgnoreCase))
        {
            string tag = actionId["Msg.AddTag:".Length..];
            bool hasTag = primaryMsg?.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase) == true;
            return Make($"🏷️ {tag}", $"🏷️{tag}",
                _ => targets.Length == 0 ? Task.CompletedTask : MouseActionService.RunMessagesAsync(hasTag ? MailMessageCommand.RemoveTag : MailMessageCommand.AddTag, targets, tag, includeSelection: false),
                disabled: targets.Length == 0,
                isActive: hasTag,
                title: $"Assign / toggle tag '{tag}'");
        }

        if (actionId.StartsWith("Msg.RemoveTag:", StringComparison.OrdinalIgnoreCase))
        {
            string tag = actionId["Msg.RemoveTag:".Length..];
            return Make($"✕ {tag}", $"✕{tag}",
                _ => targets.Length == 0 ? Task.CompletedTask : MouseActionService.RunMessagesAsync(MailMessageCommand.RemoveTag, targets, tag, includeSelection: false),
                disabled: targets.Length == 0,
                title: $"Remove tag '{tag}'");
        }

        if (actionId.StartsWith("Msg.Move:", StringComparison.OrdinalIgnoreCase))
        {
            string destFull = actionId["Msg.Move:".Length..];
            string shortFolder = destFull.Split(['/', '.']).LastOrDefault() ?? destFull;
            return Make($"📂 {shortFolder}", $"📂{shortFolder}",
                _ => targets.Length == 0 ? Task.CompletedTask : MouseActionService.RunMessagesAsync(MailMessageCommand.Move, targets, destFull, includeSelection: false),
                disabled: targets.Length == 0,
                title: $"Move to {destFull}");
        }

        if (actionId.StartsWith("Msg.Snooze:", StringComparison.OrdinalIgnoreCase))
        {
            string presetLabel = actionId["Msg.Snooze:".Length..];
            var preset = SnoozeService.Presets.FirstOrDefault(p => p.Label.Equals(presetLabel, StringComparison.OrdinalIgnoreCase));
            if (preset.Label == null) return null;
            return Make($"💤 {preset.Label}", "💤",
                _ => targets.Length == 0 ? Task.CompletedTask : MouseActionService.RunMessagesAsync(MailMessageCommand.Snooze, targets, dueUtc: preset.DueUtc(), includeSelection: false),
                disabled: targets.Length == 0,
                title: $"Snooze: {preset.Label}");
        }

        if (actionId.StartsWith("Filter.Token:", StringComparison.OrdinalIgnoreCase))
        {
            string token = actionId["Filter.Token:".Length..];
            string label = QuickFilters.FirstOrDefault(q => q.Token.Equals(token, StringComparison.OrdinalIgnoreCase)).Label ?? $"🔎 {token}";
            return Make(label, "🔎", _ => { ToggleSearchToken(token); return Task.CompletedTask; }, isActive: HasSearchToken(token), title: $"Toggle search filter {token}");
        }

        if (actionId.StartsWith("Sender.TagAll:", StringComparison.OrdinalIgnoreCase))
        {
            string tag = actionId["Sender.TagAll:".Length..];
            return Make($"🏷️ Sender: {tag}", $"🏷️{tag}",
                _ => senderAccId == null || string.IsNullOrEmpty(senderAddr) ? Task.CompletedTask : RunSenderCommandAsync(senderAccId, senderAddr, MailMessageCommand.AddTag, tag),
                disabled: senderAccId == null || string.IsNullOrEmpty(senderAddr),
                title: $"Add tag '{tag}' to all mail from sender");
        }

        if (actionId.StartsWith("Sender.RuleTag:", StringComparison.OrdinalIgnoreCase))
        {
            string tag = actionId["Sender.RuleTag:".Length..];
            return Make($"🪄 Rule tag: {tag}", $"🪄{tag}",
                _ =>
                {
                    if (senderAccId != null && !string.IsNullOrEmpty(senderAddr))
                    {
                        FilterRuleStore.UpsertSenderActionRule(senderAccId, senderAddr, new FilterAction { Type = FilterActionType.AddTag, Argument = tag });
                        MouseActionService.ReportStatus($"Rule saved: tag future mail from {senderAddr} as '{tag}'.");
                    }
                    return Task.CompletedTask;
                },
                disabled: senderAccId == null || string.IsNullOrEmpty(senderAddr));
        }

        if (actionId.StartsWith("Sender.MoveAll:", StringComparison.OrdinalIgnoreCase))
        {
            string destFull = actionId["Sender.MoveAll:".Length..];
            string shortFolder = destFull.Split(['/', '.']).LastOrDefault() ?? destFull;
            return Make($"📂 Sender → {shortFolder}", $"📂{shortFolder}",
                _ => senderAccId == null || string.IsNullOrEmpty(senderAddr) ? Task.CompletedTask : RunSenderCommandAsync(senderAccId, senderAddr, MailMessageCommand.Move, destFull),
                disabled: senderAccId == null || string.IsNullOrEmpty(senderAddr));
        }

        return actionId switch
        {
            "Msg.ToggleRead" => Make(primaryMsg?.IsUnread == false ? "📩 Unread" : "✅ Read", "✓",
                _ => targets.Length == 0 ? Task.CompletedTask : MouseActionService.RunMessagesAsync(primaryMsg?.IsUnread == false ? MailMessageCommand.Unread : MailMessageCommand.Read, targets, includeSelection: false),
                disabled: targets.Length == 0),
            "Msg.Read" => Make("✅ Mark read", "✓",
                _ => targets.Length == 0 ? Task.CompletedTask : MouseActionService.RunMessagesAsync(MailMessageCommand.Read, targets, includeSelection: false),
                disabled: targets.Length == 0),
            "Msg.Unread" => Make("📩 Mark unread", "📩",
                _ => targets.Length == 0 ? Task.CompletedTask : MouseActionService.RunMessagesAsync(MailMessageCommand.Unread, targets, includeSelection: false),
                disabled: targets.Length == 0),
            "Msg.Reply" => Make("↩️ Reply", "↩️", _ => { if (primaryMsg != null) StartReply(primaryMsg, false); return Task.CompletedTask; }, disabled: primaryMsg == null),
            "Msg.ReplyAll" => Make("↩️ Reply all", "↩️+", _ => { if (primaryMsg != null) StartReply(primaryMsg, true); return Task.CompletedTask; }, disabled: primaryMsg == null),
            "Msg.Forward" => Make("↪️ Forward", "↪️", _ => { if (primaryMsg != null) StartForward(primaryMsg); return Task.CompletedTask; }, disabled: primaryMsg == null),
            "Msg.ForwardAttachment" => Make("📎 Fwd .eml", "📎", _ => { if (primaryMsg != null) StartForwardAsAttachment(primaryMsg); return Task.CompletedTask; }, disabled: primaryMsg == null),
            "Msg.ReplyMenu" => Make("↩️ Reply ▾", "↩️", e => { if (primaryMsg != null) OpenReplyMenu(primaryMsg, e); return Task.CompletedTask; }, disabled: primaryMsg == null),
            "Msg.MarkersModal" => Make("⭐ Labels", "⭐", _ => { if (primaryMsg != null) OpenMarkersModal(primaryMsg); else OpenBulkMarkersModal(); return Task.CompletedTask; }, disabled: targets.Length == 0),
            "Msg.ToggleStar" => Make(primaryMsg?.Flags.HasFlag(MailFlags.Flagged) == true ? "⭐ Unstar" : "☆ Star", "⭐",
                _ => targets.Length == 0 ? Task.CompletedTask : MouseActionService.RunMessagesAsync(MailMessageCommand.ToggleStar, targets, includeSelection: false),
                disabled: targets.Length == 0),
            "Msg.ToggleImportant" => Make(primaryMsg?.Flags.HasFlag(MailFlags.Important) == true ? "❗ Important" : "❕ Important", "❗",
                _ => targets.Length == 0 ? Task.CompletedTask : MouseActionService.RunMessagesAsync(MailMessageCommand.ToggleImportant, targets, includeSelection: false),
                disabled: targets.Length == 0),
            "Msg.ToggleMute" => Make("🔇 Mute", "🔇",
                _ => targets.Length == 0 ? Task.CompletedTask : MouseActionService.RunMessagesAsync(MailMessageCommand.ToggleMute, targets, includeSelection: false),
                disabled: targets.Length == 0),
            "Msg.Archive" => Make("📦 Archive", "📦",
                _ => targets.Length == 0 ? Task.CompletedTask : MouseActionService.RunMessagesAsync(MailMessageCommand.Archive, targets, includeSelection: false),
                disabled: targets.Length == 0),
            "Msg.Spam" => Make("🚫 Spam", "🚫",
                _ => targets.Length == 0 ? Task.CompletedTask : MouseActionService.RunMessagesAsync(MailMessageCommand.Spam, targets, includeSelection: false),
                disabled: targets.Length == 0),
            "Msg.Restore" => Make("📥 Restore", "📥",
                _ => targets.Length == 0 ? Task.CompletedTask : MouseActionService.RunMessagesAsync(MailMessageCommand.Restore, targets, includeSelection: false),
                disabled: targets.Length == 0),
            "Msg.Trash" => Make("🗑️ Trash", "🗑️",
                _ => targets.Length == 0 ? Task.CompletedTask : MouseActionService.RunMessagesAsync(MailMessageCommand.Trash, targets, includeSelection: false),
                disabled: targets.Length == 0, isDanger: true),
            "Msg.MoveModal" => Make("📂 Move", "📂", _ => { if (primaryMsg != null) OpenMoveModal(primaryMsg); else OpenBulkMoveModal(); return Task.CompletedTask; }, disabled: targets.Length == 0),
            "Msg.SnoozeModal" => Make("⏰ Snooze", "⏰", _ => { if (primaryMsg != null) OpenSnoozeModal(primaryMsg); else OpenBulkSnoozeModal(); return Task.CompletedTask; }, disabled: targets.Length == 0),
            "Msg.Wake" => Make("☀️ Wake", "☀️",
                _ => targets.Length == 0 ? Task.CompletedTask : MouseActionService.RunMessagesAsync(MailMessageCommand.Wake, targets, includeSelection: false),
                disabled: targets.Length == 0),
            "Msg.SaveModal" => Make("💾 Save", "💾", _ => { if (primaryMsg != null) OpenSaveModal(primaryMsg); return Task.CompletedTask; }, disabled: primaryMsg == null),
            "Msg.SaveEml" => Make("📄 .eml", "📄", _ => primaryMsg == null ? Task.CompletedTask : ExportOpen(primaryMsg, false), disabled: primaryMsg == null),
            "Msg.SaveHtml" => Make("🌐 .html", "🌐", _ => primaryMsg == null ? Task.CompletedTask : ExportOpen(primaryMsg, true), disabled: primaryMsg == null),
            "Msg.ViewSource" => Make("🧾 Source", "🧾", _ => { if (primaryMsg != null) OpenMessageSource(primaryMsg); return Task.CompletedTask; }, disabled: primaryMsg == null),
            "Msg.CopySubject" => Make("📋 Subject", "📋", _ => primaryMsg == null ? Task.CompletedTask : MouseActionService.RunAsync(MouseActionService.CopyAction, primaryMsg.Subject), disabled: primaryMsg == null),
            "Msg.CopySender" or "Sender.CopyAddress" => Make("📋 Sender", "📋", _ => string.IsNullOrEmpty(senderAddr) ? Task.CompletedTask : MouseActionService.RunAsync(MouseActionService.CopyAction, senderAddr), disabled: string.IsNullOrEmpty(senderAddr)),
            "Msg.OpenPerson" or "Sender.OpenPerson" => Make("👤 Person", "👤", _ => { if (!string.IsNullOrEmpty(senderAddr)) MailUiState.OpenPerson(senderAddr); return Task.CompletedTask; }, disabled: string.IsNullOrEmpty(senderAddr)),
            "Msg.FilterSender" or "Sender.FindFrom" => Make("🔎 From sender", "🔎", _ => { if (!string.IsNullOrEmpty(senderAddr)) RunSavedSearch("from:" + senderAddr); return Task.CompletedTask; }, disabled: string.IsNullOrEmpty(senderAddr)),
            "Sender.FindTo" => Make("🔎 To sender", "🔎", _ => { if (!string.IsNullOrEmpty(senderAddr)) RunSavedSearch("to:" + senderAddr); return Task.CompletedTask; }, disabled: string.IsNullOrEmpty(senderAddr)),
            "Sender.Write" => Make("✏️ Write to sender", "✏️", _ => { if (senderAccId != null && !string.IsNullOrEmpty(senderAddr)) ComposeToSender(senderAccId, senderAddr); return Task.CompletedTask; }, disabled: senderAccId == null || string.IsNullOrEmpty(senderAddr)),
            "Sender.MarkAllRead" => Make("✓✓ Read sender", "✓✓", _ => senderAccId == null || string.IsNullOrEmpty(senderAddr) ? Task.CompletedTask : RunSenderCommandAsync(senderAccId, senderAddr, MailMessageCommand.Read), disabled: senderAccId == null || string.IsNullOrEmpty(senderAddr)),
            "Sender.TrashAll" => Make("🗑️ Trash sender", "🗑️", _ => senderAccId == null || string.IsNullOrEmpty(senderAddr) ? Task.CompletedTask : RunSenderCommandAsync(senderAccId, senderAddr, MailMessageCommand.Trash), disabled: senderAccId == null || string.IsNullOrEmpty(senderAddr), isDanger: true),
            "Sender.ArchiveAll" => Make("📦 Archive sender", "📦", _ => senderAccId == null || string.IsNullOrEmpty(senderAddr) ? Task.CompletedTask : RunSenderCommandAsync(senderAccId, senderAddr, MailMessageCommand.Archive), disabled: senderAccId == null || string.IsNullOrEmpty(senderAddr)),
            "Sender.NotifyMute" => Make("🔕 Mute sender", "🔕", _ => { if (senderAccId != null && !string.IsNullOrEmpty(senderAddr)) SetSenderNotifications(senderAccId, senderAddr, true); return Task.CompletedTask; }, disabled: senderAccId == null || string.IsNullOrEmpty(senderAddr)),
            "Sender.EditRules" => Make("🪄 Sender rules", "🪄", _ => { if (senderAccId != null && !string.IsNullOrEmpty(senderAddr)) Navigation.NavigateTo(FilterRules.LinkFor(accountId: senderAccId, senderAddress: senderAddr)); return Task.CompletedTask; }, disabled: senderAccId == null || string.IsNullOrEmpty(senderAddr)),
            "Workspace.Compose" or "Account.Compose" => Make("✏️ Compose", "✏️", _ => MouseActionService.RunAsync(MouseActionService.ComposeAction, string.Empty)),
            "Workspace.SyncAll" => Make("🔄 Check mail", "🔄", _ => MouseActionService.RunAsync(MouseActionService.SyncAction, string.Empty)),
            "Workspace.Settings" => Make("⚙️ Settings", "⚙️", _ => { Navigation.NavigateTo(NavigationConstants.Settings.Link); return Task.CompletedTask; }),
            "List.SelectMode" => Make("☑ Select", "☑", _ => { SelectionMode = !SelectionMode; return Task.CompletedTask; }, isActive: SelectionMode),
            "List.SelectAll" => Make("Select all", "☑️", _ => { SelectAllMessages(); return Task.CompletedTask; }, disabled: Rows.Count == 0),
            "List.SelectMenu" => Make("Select ▾", "☑▾", e => { OpenSelectionMenu(e); return Task.CompletedTask; }, disabled: Rows.Count == 0),
            "List.SortMenu" => Make($"⇅ {(IsSenderListView ? ListViewState.SenderSortLabel : ListViewState.MailSortLabel)} ▾", "⇅", e => { OpenSortMenu(e); return Task.CompletedTask; }),
            "List.SenderView" => Make(IsSenderListView ? "✉️ Mails" : "👥 Senders", "👥", _ => { ToggleSenderListView(); return Task.CompletedTask; }, isActive: IsSenderListView),
            "List.Shortcuts" => Make("⌨️ Shortcuts", "⌨️", _ => { OpenShortcutHelpModal(); return Task.CompletedTask; }),
            "List.ToggleQuickFilters" => Make("🎛️ Filters", "🎛️", _ => { ShowSearchTools = !ShowSearchTools; return Task.CompletedTask; }, isActive: ShowSearchTools),
            "List.ToggleAdvancedFilters" => Make("🎯 Advanced", "🎯", _ => { ToggleAdvancedFilterWindow(); return Task.CompletedTask; }, isActive: ListViewState.IsFilterWindowOpen),
            "Filter.Conversations" => Make("🧵 Conversations", "🧵", _ => { ToggleConversationMode(); return Task.CompletedTask; }, isActive: ConversationMode),
            "Filter.AllFolders" => Make("🗂️ All folders", "🗂️", _ => { ToggleSearchAllFolders(); return Task.CompletedTask; }, isActive: SearchAllFolders),
            "Nav.Prev" => Make("← Prev", "←", _ => { OpenListedMessage(-1); return Task.CompletedTask; }, disabled: ListedNavigationTarget(-1) == null),
            "Nav.Next" => Make("→ Next", "→", _ => { OpenListedMessage(1); return Task.CompletedTask; }, disabled: ListedNavigationTarget(1) == null),
            "Nav.NextUnread" => Make("📩→ Unread", "📩→", _ => { OpenListedMessage(1, unreadOnly: true); return Task.CompletedTask; }, disabled: ListedNavigationTarget(1, unreadOnly: true) == null),
            "Reader.ScaleDown" => Make("A-", "A-", _ => { StepReaderTextScale(-1); return Task.CompletedTask; }),
            "Reader.ScaleReset" => Make($"{GlobalSettings.ReaderTextScalePercent.Value}%", "100%", _ => { StepReaderTextScale(0); return Task.CompletedTask; }),
            "Reader.ScaleUp" => Make("A+", "A+", _ => { StepReaderTextScale(1); return Task.CompletedTask; }),
            _ => null
        };
    }

    internal ContextMenuItem? ResolveMenuAddedItem(
        string menuSurfaceId,
        string actionId,
        MailMessageIdentity[]? messageTargets = null,
        MailMessageIdentity? leadMessage = null,
        string? senderAccountId = null,
        string? senderAddress = null,
        MailFolderData? folderContext = null,
        MailAccountData? accountContext = null,
        AccountFolderData? accountFolderContext = null)
    {
        var targets = ResolveActiveMessageTargets(messageTargets);
        var primaryMsg = ResolvePrimaryMessage(null, leadMessage ?? targets.FirstOrDefault());
        var (sAccId, sAddr) = ResolveActiveSenderContext(senderAccountId ?? accountContext?.Id ?? folderContext?.AccountId, senderAddress);
        var activeFolder = folderContext ?? MailUiState.SelectedFolder;

        if (actionId.StartsWith("Msg.AddTag:", StringComparison.OrdinalIgnoreCase))
        {
            string tag = actionId["Msg.AddTag:".Length..];
            return new ContextMenuItem($"🏷️ {tag}",
                () => MouseActionService.RunMessagesAsync(MailMessageCommand.AddTag, targets, tag, includeSelection: false),
                Enabled: targets.Length > 0,
                ActionId: actionId);
        }

        if (actionId.StartsWith("Msg.RemoveTag:", StringComparison.OrdinalIgnoreCase))
        {
            string tag = actionId["Msg.RemoveTag:".Length..];
            return new ContextMenuItem($"✕ Remove tag: {tag}",
                () => MouseActionService.RunMessagesAsync(MailMessageCommand.RemoveTag, targets, tag, includeSelection: false),
                Enabled: targets.Length > 0,
                ActionId: actionId);
        }

        if (actionId.StartsWith("Msg.Move:", StringComparison.OrdinalIgnoreCase))
        {
            string destFull = actionId["Msg.Move:".Length..];
            return new ContextMenuItem($"📂 Move to {destFull}",
                () => MouseActionService.RunMessagesAsync(MailMessageCommand.Move, targets, destFull, includeSelection: false),
                Enabled: targets.Length > 0,
                ActionId: actionId);
        }

        if (actionId.StartsWith("Msg.Snooze:", StringComparison.OrdinalIgnoreCase))
        {
            string presetLabel = actionId["Msg.Snooze:".Length..];
            var preset = SnoozeService.Presets.FirstOrDefault(p => p.Label.Equals(presetLabel, StringComparison.OrdinalIgnoreCase));
            if (preset.Label == null) return null;
            return new ContextMenuItem($"💤 Snooze: {preset.Label}",
                () => MouseActionService.RunMessagesAsync(MailMessageCommand.Snooze, targets, dueUtc: preset.DueUtc(), includeSelection: false),
                Enabled: targets.Length > 0,
                ActionId: actionId);
        }

        if (actionId.StartsWith("Filter.Token:", StringComparison.OrdinalIgnoreCase))
        {
            string token = actionId["Filter.Token:".Length..];
            string label = QuickFilters.FirstOrDefault(q => q.Token.Equals(token, StringComparison.OrdinalIgnoreCase)).Label ?? token;
            string prefix = HasSearchToken(token) ? "✓ " : "";
            return new ContextMenuItem($"{prefix}{label}", () => ToggleSearchToken(token), actionId: actionId);
        }

        if (actionId.StartsWith("Sender.TagAll:", StringComparison.OrdinalIgnoreCase))
        {
            string tag = actionId["Sender.TagAll:".Length..];
            return new ContextMenuItem($"🏷️ Tag all from sender: {tag}",
                () => sAccId == null || string.IsNullOrEmpty(sAddr) ? Task.CompletedTask : RunSenderCommandAsync(sAccId, sAddr, MailMessageCommand.AddTag, tag),
                Enabled: sAccId != null && !string.IsNullOrEmpty(sAddr),
                ActionId: actionId);
        }

        if (actionId.StartsWith("Sender.RuleTag:", StringComparison.OrdinalIgnoreCase))
        {
            string tag = actionId["Sender.RuleTag:".Length..];
            return new ContextMenuItem($"🪄 Rule: Tag future mail '{tag}'", () =>
            {
                if (sAccId != null && !string.IsNullOrEmpty(sAddr))
                {
                    FilterRuleStore.UpsertSenderActionRule(sAccId, sAddr, new FilterAction { Type = FilterActionType.AddTag, Argument = tag });
                    MouseActionService.ReportStatus($"Rule saved: tag future mail from {sAddr} as '{tag}'.");
                }
            }, enabled: sAccId != null && !string.IsNullOrEmpty(sAddr), actionId: actionId);
        }

        return actionId switch
        {
            "Msg.ToggleRead" => new(primaryMsg?.IsUnread == false ? "📩 Mark unread" : "✅ Mark read",
                () => MouseActionService.RunMessagesAsync(primaryMsg?.IsUnread == false ? MailMessageCommand.Unread : MailMessageCommand.Read, targets, includeSelection: false),
                Enabled: targets.Length > 0, ActionId: actionId),
            "Msg.ReplyMenu" => new("↩️ Reply & Forward", Children: primaryMsg == null ? [] :
            [
                new("↩️ Reply", () => StartReply(primaryMsg, false), actionId: "Msg.Reply"),
                new("↩️ Reply all", () => StartReply(primaryMsg, true), actionId: "Msg.ReplyAll"),
                new("↪️ Forward", () => StartForward(primaryMsg), actionId: "Msg.Forward"),
                new("📎 Forward as attachment (.eml)", () => StartForwardAsAttachment(primaryMsg), actionId: "Msg.ForwardAttachment")
            ], Enabled: primaryMsg != null, ActionId: actionId),
            "Msg.MoveModal" => new("📂 Move to folder…", () => { if (primaryMsg != null) OpenMoveModal(primaryMsg); else OpenBulkMoveModal(); }, enabled: targets.Length > 0, actionId: actionId),
            "Msg.SnoozeModal" => new("⏰ Snooze until…", () => { if (primaryMsg != null) OpenSnoozeModal(primaryMsg); else OpenBulkSnoozeModal(); }, enabled: targets.Length > 0, actionId: actionId),
            "Msg.MarkersModal" => new("⭐ Star, Importance & Tags…", () => { if (primaryMsg != null) OpenMarkersModal(primaryMsg); else OpenBulkMarkersModal(); }, enabled: targets.Length > 0, actionId: actionId),
            "Msg.SaveModal" => new("💾 Save message (.eml / .html)…", () => { if (primaryMsg != null) OpenSaveModal(primaryMsg); }, enabled: primaryMsg != null, actionId: actionId),
            "List.SelectMode" => new(SelectionMode ? "✓ Selection mode" : "☑ Toggle selection mode", () => SelectionMode = !SelectionMode, actionId: actionId),
            "List.SelectAll" => new("☑️ Select all listed messages", SelectAllMessages, enabled: Rows.Count > 0, actionId: actionId),
            "List.ClearSelection" => new("✕ Clear selection", MailUiState.ClearSelection, enabled: MailUiState.SelectedMessages.Count > 0, actionId: actionId),
            "List.SenderView" => new(IsSenderListView ? "✉️ Switch to Mail List" : "👥 Switch to Sender List", ToggleSenderListView, actionId: actionId),
            "List.ToggleQuickFilters" => new(ShowSearchTools ? "🎛️ Hide quick filters" : "🎛️ Show quick filters", () => ShowSearchTools = !ShowSearchTools, actionId: actionId),
            "List.ToggleAdvancedFilters" => new("🎯 Toggle advanced filters window", ToggleAdvancedFilterWindow, actionId: actionId),
            "List.Shortcuts" => new("⌨️ Keyboard shortcuts", OpenShortcutHelpModal, actionId: actionId),
            "List.EmptyTrashOrSpam" when activeFolder is { Role: FolderRole.Trash or FolderRole.Junk } f && MailUiState.SelectedAccount is { } acc =>
                new(f.Role == FolderRole.Trash ? "🗑️ Empty Trash permanently…" : "🧹 Empty Spam permanently…", () => EmptyFolderWithConfirmationAsync(acc, f), Enabled: CachedCount > 0, ActionId: actionId),
            "Filter.Conversations" => new(ConversationMode ? "✓ 🧵 Conversations" : "🧵 Conversations", ToggleConversationMode, actionId: actionId),
            "Filter.AllFolders" => new(SearchAllFolders ? "✓ 🗂️ Search all folders" : "🗂️ Search all folders", ToggleSearchAllFolders, actionId: actionId),
            "Filter.SaveSearch" => new("⭐ Save current search", SaveCurrentSearch, enabled: !string.IsNullOrWhiteSpace(SearchText), actionId: actionId),
            "Reader.ScaleDown" => new("A- Smaller reader text", () => StepReaderTextScale(-1), actionId: actionId),
            "Reader.ScaleReset" => new($"Reset reader text ({GlobalSettings.ReaderTextScalePercent.Value}%)", () => StepReaderTextScale(0), actionId: actionId),
            "Reader.ScaleUp" => new("A+ Larger reader text", () => StepReaderTextScale(1), actionId: actionId),
            "Sidebar.NewAccountFolder" => new("📁+ New account folder", () => ModalService.OpenCreateFolder(), actionId: actionId),
            "Folder.ReadAll" when activeFolder != null => new("✓✓ Mark folder read", () => MouseActionService.ReadFolderAsync(activeFolder), Enabled: activeFolder.Selectable, ActionId: actionId),
            "Folder.Export" when activeFolder != null => new("📦 Export folder as .mbox", () => ExportFolderMailbox(activeFolder), enabled: activeFolder.Selectable, actionId: actionId),
            "Folder.Resync" when activeFolder != null => new("🔄 Redownload folder", () => ResyncFolder(activeFolder), enabled: !activeFolder.IsLocal, actionId: actionId),
            "Compose.SaveDraft" => new("💾 Save draft", SaveActiveDraft, enabled: MailUiState.ActiveCompose != null, actionId: actionId),
            "Compose.Discard" => new("🗑️ Discard draft", DiscardCompose, enabled: MailUiState.ActiveCompose != null, actionId: actionId),
            "Workspace.Compose" => new("✏️ New message", () => MouseActionService.RunAsync(MouseActionService.ComposeAction, string.Empty), ActionId: actionId),
            "Workspace.SyncAll" => new("🔄 Check mail", () => MouseActionService.RunAsync(MouseActionService.SyncAction, string.Empty), ActionId: actionId),
            "Workspace.Settings" => new("⚙️ Settings", () => Navigation.NavigateTo(NavigationConstants.Settings.Link), actionId: actionId),
            _ => null
        };
    }

    // ---- Surface Inspection for UiCustomizerModal ----

    internal SurfaceInspectionSnapshot InspectUiSurface(string surfaceId)
    {
        var promotable = BuildUniversalPromotableCatalog();

        if (UiSurfaceIds.IsContextMenu(surfaceId))
        {
            var defaultMenu = BuildSampleDefaultMenuItems(surfaceId);
            var subtreeLeaves = new Dictionary<string, ContextMenuItem>(StringComparer.OrdinalIgnoreCase);
            ContextMenuService.IndexLeafItemsRecursive(defaultMenu, subtreeLeaves);

            ContextMenuItem? ResolveMenu(string id) =>
                subtreeLeaves.TryGetValue(id, out var leaf) ? leaf : ResolveMenuAddedItem(surfaceId, id);

            var visibleMenu = UiCustomizationStore.ApplyLayout(
                surfaceId,
                defaultMenu,
                static m => m.EffectiveActionId,
                static m => m.IsFixed,
                ResolveMenu);

            var visibleSet = new HashSet<string>(visibleMenu.Select(static x => x.EffectiveActionId), StringComparer.OrdinalIgnoreCase);
            var hiddenDefaults = defaultMenu
                .Where(m => !visibleSet.Contains(m.EffectiveActionId) && UiCustomizationStore.IsHidden(surfaceId, m.EffectiveActionId))
                .Select(static m => new CatalogEntryDescriptor(m.EffectiveActionId, m.Label, "Default Menu Item", m.IsFixed))
                .ToList();

            var visibleDescriptors = visibleMenu
                .Select(static m => new CatalogEntryDescriptor(m.EffectiveActionId, m.Label, m.HasChildren ? "Submenu" : "Menu Item", m.IsFixed))
                .ToList();

            return new SurfaceInspectionSnapshot(surfaceId, visibleDescriptors, hiddenDefaults, promotable);
        }
        else
        {
            var defaultBar = BuildSampleDefaultToolbarItems(surfaceId);
            var visibleBar = UiCustomizationStore.ApplyLayout(
                surfaceId,
                defaultBar,
                static a => a.ActionId,
                static a => a.IsFixed,
                id => ResolveToolbarAddedAction(surfaceId, id));

            var visibleSet = new HashSet<string>(visibleBar.Select(static x => x.ActionId), StringComparer.OrdinalIgnoreCase);
            var hiddenDefaults = defaultBar
                .Where(a => !visibleSet.Contains(a.ActionId) && UiCustomizationStore.IsHidden(surfaceId, a.ActionId))
                .Select(static a => new CatalogEntryDescriptor(a.ActionId, a.Label, "Default Button", a.IsFixed))
                .ToList();

            var visibleDescriptors = visibleBar
                .Select(static a => new CatalogEntryDescriptor(a.ActionId, a.Label, "Button", a.IsFixed))
                .ToList();

            return new SurfaceInspectionSnapshot(surfaceId, visibleDescriptors, hiddenDefaults, promotable);
        }
    }

    IReadOnlyList<UiActionModel> BuildSampleDefaultToolbarItems(string surfaceId)
    {
        var sampleSummary = MailUiState.OpenMessage
            ?? MailUiState.FocusedMessage
            ?? FilteredSummaries.FirstOrDefault()
            ?? new MailMessageSummary { Uid = 1, Subject = "Sample", FromAddress = "sender@example.com", FromName = "Sender" };
        var sampleIdentity = MessageIdentityOf(sampleSummary)
            ?? new MailMessageIdentity(MailUiState.SelectedAccount?.Id ?? "acc", MailUiState.SelectedFolder?.FullName ?? "INBOX", sampleSummary.Uid);
        var sampleTargets = new[] { sampleIdentity };
        var sampleSender = new SenderItem(sampleSummary.FromAddress, string.IsNullOrWhiteSpace(sampleSummary.FromName) ? sampleSummary.FromAddress : sampleSummary.FromName, 1, 0, DateTime.UtcNow);
        var sampleAccount = MailUiState.SelectedAccount ?? AccountStore.Accounts.FirstOrDefault();
        var sampleFolder = MailUiState.SelectedFolder ?? (sampleAccount != null ? MessageStore.GetFolders(sampleAccount.Id).FirstOrDefault() : null);
        var sampleAccGroup = AccountFolderStore.GetRootFolders().FirstOrDefault();

        return surfaceId switch
        {
            UiSurfaceIds.ListToolbar => BuildListToolbarActions(),
            UiSurfaceIds.QuickFiltersBar => BuildQuickFilterActions(),
            UiSurfaceIds.BulkToolbar => BuildBulkToolbarActions(),
            UiSurfaceIds.MailRowHover => BuildMailRowHoverActions(sampleSummary, sampleTargets),
            UiSurfaceIds.SenderRowHover => BuildSenderRowHoverActions(sampleSender),
            UiSurfaceIds.ReaderHeaderNav => BuildReaderHeaderNavActions(),
            UiSurfaceIds.ReaderToolbar => BuildReaderToolbarActions(sampleSummary, sampleTargets),
            UiSurfaceIds.PersonActionsBar => BuildPersonActions(PersonProfileService.Build(sampleAccount?.Id ?? string.Empty, sampleSender.Address)),
            UiSurfaceIds.ComposeQuoteBar when MailUiState.ActiveCompose is { } d1 => BuildComposeQuoteActions(d1),
            UiSurfaceIds.ComposeQuoteBar when sampleAccount != null => BuildComposeQuoteActions(ComposeService.BuildNew(sampleAccount)),
            UiSurfaceIds.ComposeFooterBar when MailUiState.ActiveCompose is { } d2 => BuildComposeFooterActions(d2),
            UiSurfaceIds.ComposeFooterBar when sampleAccount != null => BuildComposeFooterActions(ComposeService.BuildNew(sampleAccount)),
            UiSurfaceIds.SidebarTop => BuildSidebarTopActions(),
            UiSurfaceIds.SidebarFolderHover when sampleAccount != null && sampleFolder != null => BuildSidebarFolderHoverActions(sampleAccount, sampleFolder),
            UiSurfaceIds.SidebarAccountFolderHover when sampleAccGroup != null => BuildSidebarAccountFolderHoverActions(sampleAccGroup, () => Task.CompletedTask, false, null),
            _ => []
        };
    }

    IReadOnlyList<ContextMenuItem> BuildSampleDefaultMenuItems(string menuSurfaceId)
    {
        var sampleAccount = MailUiState.SelectedAccount ?? AccountStore.Accounts.FirstOrDefault();
        var sampleFolder = MailUiState.SelectedFolder ?? (sampleAccount != null ? MessageStore.GetFolders(sampleAccount.Id).FirstOrDefault() : null);
        var sampleSummary = MailUiState.OpenMessage ?? MailUiState.FocusedMessage ?? FilteredSummaries.FirstOrDefault();
        var sampleLead = (sampleSummary != null ? MessageIdentityOf(sampleSummary) : null)
            ?? new MailMessageIdentity(sampleAccount?.Id ?? "acc", sampleFolder?.FullName ?? "INBOX", sampleSummary?.Uid ?? 1);
        var sampleAccGroup = AccountFolderStore.GetRootFolders().FirstOrDefault();
        string sampleAddress = MailUiState.OpenPersonAddress ?? sampleSummary?.FromAddress ?? "sender@example.com";

        return menuSurfaceId switch
        {
            UiSurfaceIds.MessageContextMenu => BuildMessageMenuItems([sampleLead], sampleLead, oneConversation: true),
            UiSurfaceIds.SenderContextMenu when sampleAccount != null => SenderMenuItems(sampleAccount.Id, sampleAddress),
            UiSurfaceIds.FolderContextMenu when sampleFolder != null => BuildFolderMenuItems(sampleFolder),
            UiSurfaceIds.AccountContextMenu when sampleAccount != null => MailMenuItems.ForAccount(sampleAccount, () => DeleteFastMailAccountAsync(sampleAccount)),
            UiSurfaceIds.AccountFolderContextMenu when sampleAccGroup != null => MailMenuItems.ForAccountFolder(sampleAccGroup),
            UiSurfaceIds.WorkspaceContextMenu => BuildWorkspaceMenuItems(),
            _ => []
        };
    }

    IReadOnlyList<CatalogEntryDescriptor> BuildUniversalPromotableCatalog()
    {
        List<CatalogEntryDescriptor> list = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string id, string label, string category)
        {
            if (seen.Add(id))
                list.Add(new CatalogEntryDescriptor(id, label, category));
        }

        // 1. User Tags (1-Click Assign & Remove & Sender Tagging & Sender Rule)
        foreach (string tag in TagStore.AllTags)
        {
            Add($"Msg.AddTag:{tag}", $"🏷️ {tag}", "1-Click Tag");
            Add($"Msg.RemoveTag:{tag}", $"✕ Remove tag: {tag}", "1-Click Untag");
            Add($"Sender.TagAll:{tag}", $"🏷️ Tag all from sender: {tag}", "Sender Tag");
            Add($"Sender.RuleTag:{tag}", $"🪄 Rule: Tag future '{tag}'", "Sender Rule");
        }

        // 2. Account Folders (1-Click Move)
        if (MailUiState.SelectedAccount is { } acc)
        {
            foreach (var folder in MessageStore.GetFolders(acc.Id).Where(static f => f.Selectable && f.Role is not (FolderRole.Drafts or FolderRole.Outbox)))
            {
                Add($"Msg.Move:{folder.FullName}", $"📂 Move to {FolderLabel(folder)}", "1-Click Move");
                Add($"Sender.MoveAll:{folder.FullName}", $"📂 Move sender mail to {FolderLabel(folder)}", "Sender Move");
            }
        }

        // 3. Snooze Presets
        foreach (var preset in SnoozeService.Presets)
        {
            Add($"Msg.Snooze:{preset.Label}", $"💤 Snooze: {preset.Label}", "1-Click Snooze");
        }

        // 4. Core Message Commands
        Add("Msg.ToggleRead", "✅ Toggle Read / Unread", "Message");
        Add("Msg.Read", "✅ Mark Read", "Message");
        Add("Msg.Unread", "📩 Mark Unread", "Message");
        Add("Msg.Reply", "↩️ Reply", "Message");
        Add("Msg.ReplyAll", "↩️ Reply All", "Message");
        Add("Msg.Forward", "↪️ Forward", "Message");
        Add("Msg.ForwardAttachment", "📎 Forward as .eml", "Message");
        Add("Msg.MarkersModal", "⭐ Labels & Tags Modal", "Message");
        Add("Msg.ToggleStar", "⭐ Toggle Star", "Message");
        Add("Msg.ToggleImportant", "❗ Toggle Importance", "Message");
        Add("Msg.ToggleMute", "🔇 Toggle Mute", "Message");
        Add("Msg.Archive", "📦 Archive", "Message");
        Add("Msg.MoveModal", "📂 Move Modal…", "Message");
        Add("Msg.SnoozeModal", "⏰ Snooze Modal…", "Message");
        Add("Msg.Wake", "☀️ Wake Snoozed", "Message");
        Add("Msg.Spam", "🚫 Move to Spam", "Message");
        Add("Msg.Restore", "📥 Restore to Inbox", "Message");
        Add("Msg.Trash", "🗑️ Move to Trash / Delete", "Message");
        Add("Msg.SaveModal", "💾 Save (.eml / .html)…", "Message");
        Add("Msg.SaveEml", "📄 Quick Export .eml", "Message");
        Add("Msg.SaveHtml", "🌐 Quick Export .html", "Message");
        Add("Msg.ViewSource", "🧾 View Message Source", "Message");
        Add("Msg.CopySubject", "📋 Copy Subject", "Message");
        Add("Msg.CopySender", "📋 Copy Sender Address", "Message");

        // 5. Sender Commands
        Add("Sender.OpenPerson", "👤 Open Sender Profile", "Sender");
        Add("Sender.Write", "✏️ Write to Sender", "Sender");
        Add("Sender.FindFrom", "🔎 Find Mail from Sender", "Sender");
        Add("Sender.FindTo", "🔎 Find Mail to Sender", "Sender");
        Add("Sender.MarkAllRead", "✓✓ Mark All from Sender Read", "Sender");
        Add("Sender.ArchiveAll", "📦 Archive All from Sender", "Sender");
        Add("Sender.TrashAll", "🗑️ Trash All from Sender", "Sender");
        Add("Sender.NotifyMute", "🔕 Mute Sender Notifications", "Sender");
        Add("Sender.EditRules", "🪄 Edit Sender Rules", "Sender");

        // 6. List, Filters, Navigation & Workspace
        foreach (var (label, token) in QuickFilters)
            Add($"Filter.Token:{token}", label, "Quick Filter");
        Add("Filter.Conversations", "🧵 Conversations Toggle", "List & Filter");
        Add("Filter.AllFolders", "🗂️ Search All Folders", "List & Filter");
        Add("List.SelectMode", "☑ Selection Mode", "List & Filter");
        Add("List.SelectAll", "☑️ Select All Listed", "List & Filter");
        Add("List.SelectMenu", "Select ▾ Menu", "List & Filter");
        Add("List.SortMenu", "⇅ Sort ▾ Menu", "List & Filter");
        Add("List.SenderView", "👥 Toggle Senders / Mails View", "List & Filter");
        Add("List.ToggleQuickFilters", "🎛️ Toggle Quick Filters Bar", "List & Filter");
        Add("List.ToggleAdvancedFilters", "🎯 Toggle Advanced Filters Window", "List & Filter");
        Add("List.Shortcuts", "⌨️ Keyboard Shortcuts", "Workspace");
        Add("Workspace.Compose", "✏️ Compose New Message", "Workspace");
        Add("Workspace.SyncAll", "🔄 Check Mail Now", "Workspace");
        Add("Workspace.Settings", "⚙️ Open Settings", "Workspace");
        Add("Nav.Prev", "← Previous Message", "Navigation");
        Add("Nav.Next", "→ Next Message", "Navigation");
        Add("Nav.NextUnread", "📩→ Next Unread", "Navigation");
        Add("Reader.ScaleDown", "A- Smaller Reader Text", "Reader");
        Add("Reader.ScaleReset", "100% Reset Reader Text", "Reader");
        Add("Reader.ScaleUp", "A+ Larger Reader Text", "Reader");

        return list;
    }
}
