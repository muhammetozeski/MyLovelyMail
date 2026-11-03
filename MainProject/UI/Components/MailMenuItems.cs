using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.UI.Pages;

namespace MyLovelyMail.MainProject.UI.Components;

/// <summary>Account and account-folder commands shared by the mail sidebar and account cards in Settings.</summary>
public static class MailMenuItems
{
    /// <summary>Captures one account for all presentation surfaces without copying its commands into each page.</summary>
    /// <param name="account">Clicked account.</param>
    /// <returns>Existing account operations in menu order, starting with Title &amp; Note view/edit at the very top.</returns>
    public static IReadOnlyList<ContextMenuItem> ForAccount(MailAccountData account, Func<Task>? deleteFastMail = null)
    {
        string noteMenuLabel = !string.IsNullOrWhiteSpace(account.Title)
            ? $"📝 {account.Title} — View / edit title & note"
            : "📝 Title & note (view / edit)";

        var folderChoices = AccountFolderStore.GetFlattenedTree()
            .Select(entry => new ContextMenuItem(
                $"{new string('—', entry.Depth)} 📁 {entry.Folder.Name}",
                () => AccountStore.MoveToFolder(account.Id, entry.Folder.Id),
                enabled: !string.Equals(AccountFolderStore.NormalizeFolderId(account.FolderId), entry.Folder.Id, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return
        [
            new(noteMenuLabel, () => ModalService.OpenAccountNote(account.Id)),
            new("📂 Open account", () => { MailUiState.SelectAccount(account, restoreFolder: true); NavigationBridge.TryNavigate(NavigationConstants.Mail.Link); }),
            new("🔄 Check this account", () => MouseActionService.RunAsync(MouseActionService.AccountSyncAction, account.Id)),
            new("✏️ New message from this account", () => MouseActionService.RunAsync(MouseActionService.ComposeAction, account.Id)),
            new("⧉ Copy email address", () => MouseActionService.RunAsync(MouseActionService.CopyAction, account.EmailAddress)),
            new("📁 Move to account folder", Children: folderChoices),
            new(account.Enabled ? "⏸ Pause account" : "▶ Resume account", () => { account.Enabled = !account.Enabled; AccountStore.Save(account); }),
            new("↑ Move account up", () => AccountStore.Move(account.Id, -1)),
            new("↓ Move account down", () => AccountStore.Move(account.Id, 1)),
            new("⚙️ Account settings", () => NavigationBridge.TryNavigate(AccountSettingsPage.RouteFor(account.Id))),
            new("⚑ Filter rules for this account", () => NavigationBridge.TryNavigate(FilterRules.LinkFor(accountId: account.Id))),
            .. (account.Protocol == IncomingProtocol.FastMail && deleteFastMail != null && OperatingSystem.IsWindows()
                ? new[] { new ContextMenuItem("🗑 Delete Fast Mail account", deleteFastMail) } : Array.Empty<ContextMenuItem>())
        ];
    }

    public static IReadOnlyList<ContextMenuItem> ForAccountFolder(AccountFolderData folder)
    {
        bool isDefault = AccountFolderStore.IsDefaultFolder(folder.Id);
        return
        [
            new("➕ Add mail account here", () => ModalService.OpenAddAccount(folder.Id)),
            new("⚡ Add Fast Mail here", () => ModalService.OpenFastMail(folder.Id)),
            new("⚑ Filter rules for this folder", () => NavigationBridge.TryNavigate(FilterRules.LinkFor(accountFolderId: folder.Id))),
            new("📁+ New subfolder", () => ModalService.OpenCreateFolder(folder.Id)),
            new("✏️ Rename folder", () => ModalService.OpenRenameFolder(folder.Id)),
            new("🗑️ Delete folder", () => { AccountFolderStore.DeleteFolder(folder.Id); }, enabled: !isDefault)
        ];
    }
}
