using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.UI.Pages;

namespace MyLovelyMail.MainProject.UI.Components;

/// <summary>Account commands shared by the mail sidebar and account cards in Settings.</summary>
public static class MailMenuItems
{
    /// <summary>Captures one account for all presentation surfaces without copying its commands into each page.</summary>
    /// <param name="account">Clicked account.</param>
    /// <returns>Existing account operations in menu order.</returns>
    public static IReadOnlyList<ContextMenuItem> ForAccount(MailAccountData account) => [
        new("📂 Open account", () => { MailUiState.SelectAccount(account, restoreFolder: true); NavigationBridge.TryNavigate(NavigationConstants.Mail.Link); }),
        new("🔄 Check this account", () => MouseActionService.RunAsync(MouseActionService.AccountSyncAction, account.Id)),
        new("✏️ New message from this account", () => MouseActionService.RunAsync(MouseActionService.ComposeAction, account.Id)),
        new("⧉ Copy email address", () => MouseActionService.RunAsync(MouseActionService.CopyAction, account.EmailAddress)),
        new(account.Enabled ? "⏸ Pause account" : "▶ Resume account", () => { account.Enabled = !account.Enabled; AccountStore.Save(account); }),
        new("↑ Move account up", () => AccountStore.Move(account.Id, -1)),
        new("↓ Move account down", () => AccountStore.Move(account.Id, 1)),
        new("⚙️ Account settings", () => NavigationBridge.TryNavigate(AccountSettingsPage.RouteFor(account.Id)))
    ];
}
