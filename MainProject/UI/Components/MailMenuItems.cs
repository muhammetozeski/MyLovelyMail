using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.UI.Pages;

namespace MyLovelyMail.MainProject.UI.Components;

/// <summary>Builds shared context menu item lists for accounts and account folders.</summary>
internal static class MailMenuItems
{
    public static IReadOnlyList<ContextMenuItem> ForAccount(MailAccountData account, Func<Task>? onDeleteFastMail = null)
    {
        List<ContextMenuItem> items =
        [
            new("📬 Select account", () => MailUiState.SelectAccount(account, restoreFolder: true), actionId: "Account.Select"),
            new("✏️ New message from this account", () => MouseActionService.RunAsync(MouseActionService.ComposeAction, account.Id), actionId: "Account.Compose"),
            new("🔄 Sync this account now", () => MouseActionService.RunAsync(MouseActionService.AccountSyncAction, account.Id), actionId: "Account.Sync"),
            new("📁+ New mail folder…", () => ModalService.OpenCreateMailFolder(account.Id), actionId: "Account.NewMailFolder"),
            new("🏷️ Edit title, note & folder", () => ModalService.OpenAccountNote(account.Id), actionId: "Account.EditNote"),
            new("⚙️ Account settings", () => NavigationBridge.TryNavigate(AccountSettingsPage.RouteFor(account.Id)), actionId: "Account.Settings"),
            new("🪄 Rules for this account", () => NavigationBridge.TryNavigate(FilterRules.LinkFor(accountId: account.Id)), actionId: "Account.Rules"),
            new("📋 Copy email address", () => MouseActionService.RunAsync(MouseActionService.CopyAction, account.EmailAddress), actionId: "Account.CopyEmail")
        ];
        if (account.Protocol == IncomingProtocol.FastMail && onDeleteFastMail != null)
            items.Add(new("🗑️ Delete Fast Mail (Encrypted to Recycle Bin)", onDeleteFastMail, ActionId: "Account.DeleteFastMail"));
        return items;
    }

    public static IReadOnlyList<ContextMenuItem> ForAccountFolder(AccountFolderData folder)
    {
        bool isDefault = AccountFolderStore.IsDefaultFolder(folder.Id);
        return
        [
            new(folder.IsExpanded ? "▾ Collapse folder" : "▸ Expand folder", () => AccountFolderStore.ToggleExpanded(folder.Id), actionId: "AccGroup.ToggleExpand"),
            new("➕ Add subfolder", () => ModalService.OpenCreateFolder(folder.Id), actionId: "AccGroup.AddSubfolder"),
            new("✏️ Rename folder", () => ModalService.OpenRenameFolder(folder.Id), actionId: "AccGroup.Rename"),
            new("➕ Add account here", () => ModalService.OpenAddAccount(folder.Id), actionId: "AccGroup.AddAccount"),
            new("⚡ New Fast Mail here", () => ModalService.OpenFastMail(folder.Id), actionId: "AccGroup.NewFastMail"),
            new("🪄 Rules for this account group", () => NavigationBridge.TryNavigate(FilterRules.LinkFor(accountFolderId: folder.Id)), actionId: "AccGroup.Rules"),
            new("🗑️ Delete folder…", () => ConfirmAndTrashAccountGroupAsync(folder), Enabled: !isDefault, ActionId: "AccGroup.Delete")
        ];
    }

    public static async Task ConfirmAndTrashAccountGroupAsync(AccountFolderData folder)
    {
        if (AccountFolderStore.IsDefaultFolder(folder.Id)) return;

        var (autoPurge, days) = TrashRetentionService.ResolveDeletedFoldersPolicy();
        string parentName = folder.ParentFolderId is { Length: > 0 } pid && AccountFolderStore.GetById(pid) is { } parent
            ? parent.Name
            : "Accounts";
        string retentionLine = autoPurge
            ? $"It will move to Deleted Folders Trash and be permanently deleted after {days} day(s)."
            : "It will move to Deleted Folders Trash (auto-delete is currently off).";

        bool confirmed = await ModalService.ConfirmAsync(
            $"Delete folder '{folder.Name}'?",
            $"Are you sure you want to delete account group folder '{folder.Name}'? Any accounts inside will remain safe under '{parentName}'.\n\n{retentionLine}",
            confirmLabel: "🗑️ Move to Folder Trash",
            cancelLabel: "Cancel",
            isDanger: true);

        if (!confirmed) return;

        if (AccountFolderStore.MoveFolderToTrash(folder.Id) != null)
            MouseActionService.ReportStatus($"Moved folder '{folder.Name}' to Deleted Folders Trash.");
    }
}
