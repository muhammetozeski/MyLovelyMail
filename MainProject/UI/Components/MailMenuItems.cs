using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.UI.Pages;

namespace MyLovelyMail.MainProject.UI.Components;

/// <summary>Describes a pinned reader toolbar action shared between the toolbar strip and context menu pin system.</summary>
public sealed record PinnedReaderAction(
    string PinId,
    string ToolbarLabel,
    string Label,
    Func<Task> Execute);

/// <summary>Builds shared menu item lists and manages pinned reader toolbar actions.</summary>
internal static class MailMenuItems
{
    public static event Action? OnPinnedActionsChanged;

    /// <summary>Returns the current ordered list of pinned reader action IDs from settings.</summary>
    public static IReadOnlyList<string> GetPinnedActionIds()
    {
        string raw = Stores.Settings.PinnedReaderActions.Value ?? string.Empty;
        return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Whether the specified action ID is currently pinned to the reader toolbar.</summary>
    public static bool IsActionPinned(string actionId) =>
        GetPinnedActionIds().Contains(actionId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Toggles whether <paramref name="actionId"/> is pinned to the reader toolbar and persists the setting.</summary>
    public static bool TogglePinAction(string actionId)
    {
        var list = GetPinnedActionIds().ToList();
        int idx = list.FindIndex(id => string.Equals(id, actionId, StringComparison.OrdinalIgnoreCase));
        bool nowPinned;
        if (idx >= 0)
        {
            list.RemoveAt(idx);
            nowPinned = false;
        }
        else
        {
            list.Add(actionId);
            nowPinned = true;
        }
        Stores.Settings.PinnedReaderActions.Set(string.Join(",", list));
        SettingsManager.SaveSettings();
        OnPinnedActionsChanged?.Invoke();
        Events.MainEvents.Trigger("OnStyleChanged");
        return nowPinned;
    }

    /// <summary>Builds the pinned reader action buttons for the currently open message in user-pinned order.</summary>
    public static IReadOnlyList<PinnedReaderAction> BuildPinnedReaderActions(Mail view, MailMessageSummary open)
    {
        var pinnedIds = GetPinnedActionIds();
        if (pinnedIds.Count == 0) return [];

        var targets = view.MessageTargets(open);
        bool isTrash = view.ResolveFolderRole(open) == FolderRole.Trash;
        bool isStarred = open.Flags.HasFlag(MailFlags.Flagged);
        bool isImportant = open.Flags.HasFlag(MailFlags.Important);

        var catalog = new Dictionary<string, PinnedReaderAction>(StringComparer.OrdinalIgnoreCase)
        {
            ["toggle-read"] = new("toggle-read",
                open.IsUnread ? "✅ Read" : "📩 Unread",
                open.IsUnread ? "Mark read" : "Mark unread",
                () => MouseActionService.RunMessagesAsync(open.IsUnread ? MailMessageCommand.Read : MailMessageCommand.Unread, targets, includeSelection: false)),
            ["reply"] = new("reply", "↩️ Reply", "Reply", () => { view.StartReply(open, false); return Task.CompletedTask; }),
            ["reply-all"] = new("reply-all", "↩️ Reply all", "Reply all", () => { view.StartReply(open, true); return Task.CompletedTask; }),
            ["forward"] = new("forward", "↪️ Forward", "Forward", () => { view.StartForward(open); return Task.CompletedTask; }),
            ["forward-attachment"] = new("forward-attachment", "📎 Fwd .eml", "Forward as attachment (.eml)", () => { view.StartForwardAsAttachment(open); return Task.CompletedTask; }),
            ["markers"] = new("markers", "⭐ Labels", "Star, Importance & Tags", () => { view.OpenMarkersModal(open); return Task.CompletedTask; }),
            ["star"] = new("star", isStarred ? "⭐ Unstar" : "☆ Star", "Toggle star", () => MouseActionService.RunMessagesAsync(MailMessageCommand.ToggleStar, targets, includeSelection: false)),
            ["important"] = new("important", isImportant ? "❗ Unmark" : "❕ Important", "Toggle importance", () => MouseActionService.RunMessagesAsync(MailMessageCommand.ToggleImportant, targets, includeSelection: false)),
            ["archive"] = new("archive", "📦 Archive", "Archive", () => MouseActionService.RunMessagesAsync(MailMessageCommand.Archive, targets, includeSelection: false)),
            ["move"] = new("move", "📂 Move", "Move to folder", () => { view.OpenMoveModal(open); return Task.CompletedTask; }),
            ["snooze"] = new("snooze", "⏰ Snooze", "Snooze until…", () => { view.OpenSnoozeModal(open); return Task.CompletedTask; }),
            ["trash"] = new("trash", isTrash ? "🗑️ Delete forever" : "🗑️ Trash", isTrash ? "Delete permanently" : "Move to Trash",
                () => MouseActionService.RunMessagesAsync(MailMessageCommand.Trash, targets, includeSelection: false)),
            ["spam"] = new("spam", "🚫 Spam", "Move to Spam", () => MouseActionService.RunMessagesAsync(MailMessageCommand.Spam, targets, includeSelection: false)),
            ["restore"] = new("restore", "📥 Restore", "Restore to Inbox", () => MouseActionService.RunMessagesAsync(MailMessageCommand.Restore, targets, includeSelection: false)),
            ["save"] = new("save", "💾 Save", "Save message (.eml / .html)", () => { view.OpenSaveModal(open); return Task.CompletedTask; }),
            ["save-eml"] = new("save-eml", "📄 .eml", "Save as .eml", () => { view.OpenSaveModal(open, "eml"); return Task.CompletedTask; }),
            ["save-html"] = new("save-html", "🌐 .html", "Save as .html", () => { view.OpenSaveModal(open, "html"); return Task.CompletedTask; }),
            ["source"] = new("source", "🧾 Source", "View message source", () => { view.OpenMessageSource(open); return Task.CompletedTask; }),
            ["person-details"] = new("person-details", "👤 Person", "Sender profile", () => { MailUiState.OpenPerson(open.FromAddress); return Task.CompletedTask; }),
            ["find-sender"] = new("find-sender", "🔎 Sender mail", "Find mail from sender", () => { view.RunSavedSearch("from:" + open.FromAddress); return Task.CompletedTask; })
        };

        List<PinnedReaderAction> result = [];
        foreach (string id in pinnedIds)
        {
            if (catalog.TryGetValue(id, out var action))
                result.Add(action);
        }
        return result;
    }

    public static IReadOnlyList<ContextMenuItem> ForAccount(MailAccountData account, Func<Task>? onDeleteFastMail = null)
    {
        List<ContextMenuItem> items =
        [
            new("📬 Select account", () => MailUiState.SelectAccount(account, restoreFolder: true)),
            new("✏️ New message from this account", () => MouseActionService.RunAsync(MouseActionService.ComposeAction, account.Id)),
            new("🔄 Sync this account now", () => MouseActionService.RunAsync(MouseActionService.AccountSyncAction, account.Id)),
            new("🏷️ Edit title, note & folder", () => ModalService.OpenAccountNote(account.Id)),
            new("⚙️ Account settings", () => NavigationBridge.TryNavigate(AccountSettingsPage.RouteFor(account.Id))),
            new("🪄 Rules for this account", () => NavigationBridge.TryNavigate(FilterRules.LinkFor(accountId: account.Id))),
            new("📋 Copy email address", () => MouseActionService.RunAsync(MouseActionService.CopyAction, account.EmailAddress))
        ];
        if (account.Protocol == IncomingProtocol.FastMail && onDeleteFastMail != null)
            items.Add(new("🗑️ Delete Fast Mail (Encrypted to Recycle Bin)", onDeleteFastMail));
        return items;
    }

    public static IReadOnlyList<ContextMenuItem> ForAccountFolder(AccountFolderData folder)
    {
        bool isDefault = AccountFolderStore.IsDefaultFolder(folder.Id);
        return
        [
            new(folder.IsExpanded ? "▾ Collapse folder" : "▸ Expand folder", () => AccountFolderStore.ToggleExpanded(folder.Id)),
            new("➕ Add subfolder", () => ModalService.OpenCreateFolder(folder.Id)),
            new("✏️ Rename folder", () => ModalService.OpenRenameFolder(folder.Id)),
            new("➕ Add account here", () => ModalService.OpenAddAccount(folder.Id)),
            new("⚡ New Fast Mail here", () => ModalService.OpenFastMail(folder.Id)),
            new("🪄 Rules for this account group", () => NavigationBridge.TryNavigate(FilterRules.LinkFor(accountFolderId: folder.Id))),
            new("🗑️ Delete folder (keep accounts)", () => { AccountFolderStore.DeleteFolder(folder.Id); return Task.CompletedTask; }, Enabled: !isDefault)
        ];
    }
}
