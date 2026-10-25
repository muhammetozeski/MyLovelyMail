using System.Globalization;
using Microsoft.AspNetCore.Components.Web;
using MyLovelyMail.MainProject.Constants.ThemeConstants;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.UI.Pages;

public partial class Mail
{
    const bool IsFolderLogEnabled = true;
    internal MailFolderData? FolderMenuTarget;
    internal bool FolderMuteChoicesOpen;
    internal string FolderMenuPosition = string.Empty;

    /// <summary>The menu reuses the existing mail operations instead of implementing a second folder workflow.</summary>
    internal enum FolderMenuAction
    {
        Open, Sync, Backfill, Resync, MarkRead, Export, SearchUnread,
        Settings, AccountSettings, GlobalSettings, ToggleUnreadBadge, ResetPreferences
    }

    /// <summary>Opens a menu for the clicked folder without changing the currently selected mailbox.</summary>
    /// <param name="folder">Exact account/folder target, retained even when another folder is open.</param>
    /// <param name="pointer">Pointer location in the viewport; CSS clamps the menu to its edges.</param>
    internal void OpenFolderMenu(MailFolderData folder, MouseEventArgs pointer)
    {
        FolderMenuTarget = folder;
        FolderMuteChoicesOpen = false;
        int margin = AppMeasures.Space.Px8;
        int width = AppMeasures.Size.Px280;
        int height = AppMeasures.Size.Px400;
        FolderMenuPosition = string.Create(CultureInfo.InvariantCulture,
            $"left:clamp({margin}px,{pointer.ClientX}px,max({margin}px,calc(100vw - {width + margin}px)));top:clamp({margin}px,{pointer.ClientY}px,max({margin}px,calc(100dvh - {height + margin}px))); ");
        Log($"Folder menu opened: account={folder.AccountId}; folder='{folder.FullName}'.", Run: IsFolderLogEnabled);
    }

    /// <summary>Dismisses both the main menu and its duration choices.</summary>
    internal void CloseFolderMenu()
    {
        FolderMenuTarget = null;
        FolderMuteChoicesOpen = false;
    }

    /// <summary>Applies only a notification deadline; messages, unread flags and synchronization stay untouched.</summary>
    /// <param name="minutes">A preset duration, or null for a permanent mute.</param>
    /// <param name="unmute">Explicitly enables notifications even when Spam inherits a mute.</param>
    internal void SetFolderMute(int? minutes = null, bool unmute = false)
    {
        if (FolderMenuTarget is not { } folder || folder.Role == FolderRole.Trash) return;
        var preferences = FolderSettingsStore.GetOverrides(folder.Role, folder.AccountId, folder.FullName) with
        {
            MutedUntilUtc = unmute ? DateTimeOffset.MinValue
                : minutes is { } duration ? DateTimeOffset.UtcNow.AddMinutes(duration) : DateTimeOffset.MaxValue
        };
        bool saved = FolderSettingsStore.Save(folder.Role, preferences, folder.AccountId, folder.FullName);
        FolderActionStatus = saved ? unmute ? "Folder unmuted." : "Folder muted."
            : "Could not save folder settings. See the log for details.";
        CloseFolderMenu();
    }

    /// <summary>Dispatches one folder command through its existing owner and reports any final failure.</summary>
    /// <param name="action">Selected menu command.</param>
    internal void RunFolderMenuAction(FolderMenuAction action)
    {
        if (FolderMenuTarget is not { } folder) return;
        CloseFolderMenu();
        Log($"Folder action started: action={action}; account={folder.AccountId}; folder='{folder.FullName}'.", Run: IsFolderLogEnabled);
        try
        {
            FolderActionStatus = null;
            switch (action)
            {
                case FolderMenuAction.Open:
                    MailUiState.SelectFolder(folder);
                    break;
                case FolderMenuAction.Sync:
                case FolderMenuAction.Backfill:
                    if (AccountStore.GetById(folder.AccountId) is { } account)
                        ImapSyncService.KickFolderSync(account, folder.FullName,
                            backfillCount: action == FolderMenuAction.Backfill ? BackfillBatchSize : null);
                    break;
                case FolderMenuAction.Resync:
                    ResyncFolder(folder);
                    break;
                case FolderMenuAction.MarkRead:
                    MarkFolderRead(folder);
                    break;
                case FolderMenuAction.Export:
                    ExportFolderMailbox(folder);
                    break;
                case FolderMenuAction.SearchUnread:
                    MailUiState.SelectFolder(folder);
                    SearchAllFolders = false;
                    SearchText = "is:unread";
                    break;
                case FolderMenuAction.Settings:
                    Navigation.NavigateTo($"{AccountSettingsPage.RouteFor(folder.AccountId)}?folder={Uri.EscapeDataString(folder.FullName)}#folder-settings");
                    break;
                case FolderMenuAction.AccountSettings:
                    Navigation.NavigateTo(AccountSettingsPage.RouteFor(folder.AccountId));
                    break;
                case FolderMenuAction.GlobalSettings:
                    Navigation.NavigateTo($"{NavigationConstants.Settings.Link}#folder-settings");
                    break;
                case FolderMenuAction.ToggleUnreadBadge:
                    SaveFolderMenuPreferences(folder, FolderSettingsStore.GetOverrides(folder.Role, folder.AccountId, folder.FullName)
                        with { ShowUnreadBadge = !FolderSettingsStore.ShowsUnreadCount(folder) });
                    break;
                case FolderMenuAction.ResetPreferences:
                    SaveFolderMenuPreferences(folder, null);
                    break;
            }
            Log($"Folder action finished: action={action}; folder='{folder.FullName}'.", Run: IsFolderLogEnabled);
        }
        catch (Exception exception)
        {
            FolderActionStatus = $"Could not complete folder action: {exception.Message}";
            Log($"Folder action failed: action={action}; account={folder.AccountId}; folder='{folder.FullName}'; {exception}", LogLevel.Error, Run: IsFolderLogEnabled);
        }
    }

    /// <summary>Saves a menu edit and exposes persistence failure in the sidebar.</summary>
    /// <param name="folder">Exact folder target.</param>
    /// <param name="preferences">Overrides, or null to restore inheritance.</param>
    void SaveFolderMenuPreferences(MailFolderData folder, FolderPreferences? preferences)
    {
        FolderActionStatus = FolderSettingsStore.Save(folder.Role, preferences, folder.AccountId, folder.FullName)
            ? "Folder settings saved." : "Could not save folder settings. See the log for details.";
    }
}
