using Microsoft.AspNetCore.Components.Web;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.UI.Components;

namespace MyLovelyMail.MainProject.UI.Pages;

public partial class Mail
{
    const bool IsFolderLogEnabled = true;

    /// <summary>The folder menu reuses the existing mail operation owners.</summary>
    internal enum FolderMenuAction
    {
        Open, Sync, Backfill, Resync, Export, SearchUnread,
        Settings, AccountSettings, GlobalSettings, ToggleUnreadBadge, ResetPreferences
    }

    /// <summary>Captures the right-clicked folder without changing the selected mailbox.</summary>
    /// <param name="folder">Exact account/folder scope.</param>
    /// <param name="pointer">Menu position.</param>
    internal void OpenFolderMenu(MailFolderData folder, MouseEventArgs pointer)
    {
        bool remote = folder.Selectable && !folder.IsLocal
            && AccountStore.GetById(folder.AccountId)?.Protocol == IncomingProtocol.Imap;
        ContextMenuItem Action(string label, FolderMenuAction action, bool enabled = true) =>
            new(label, () => RunFolderMenuAction(folder, action), enabled);
        List<ContextMenuItem> items = [Action("📂 Open folder", FolderMenuAction.Open, folder.Selectable)];
        if (folder.Role == FolderRole.Trash)
            items.Add(new("🔕 Trash notifications are always off", Enabled: false));
        else
        {
            bool muted = FolderSettingsStore.IsMuted(folder);
            List<ContextMenuItem> durations = [.. FolderSettingsPanel.MutePeriods.Select(period =>
                new ContextMenuItem($"Mute for {period.Label}", () => SetFolderMute(folder, period.Minutes)))];
            durations.Add(new("Mute forever", () => SetFolderMute(folder)));
            items.Add(new(muted ? "🔔 Unmute" : "🔕 Mute", () => SetFolderMute(folder, unmute: muted), children: durations));
            items.Add(new(FolderSettingsPanel.MuteStatus(FolderSettingsStore.Resolve(folder.Role, folder.AccountId, folder.FullName)), Enabled: false));
            items.Add(Action(FolderSettingsStore.ShowsUnreadCount(folder) ? "Hide unread badges" : "Show unread badges", FolderMenuAction.ToggleUnreadBadge));
        }
        items.AddRange([
            Action("🔄 Check for new mail", FolderMenuAction.Sync, remote),
            Action("📜 Download older mail", FolderMenuAction.Backfill, remote),
            Action("↻ Redownload folder", FolderMenuAction.Resync, remote),
            new("📖 Mark all cached mail read", () => MouseActionService.ReadFolderAsync(folder), folder.Selectable),
            Action("🔍 Find unread mail", FolderMenuAction.SearchUnread, folder.Selectable),
            Action("📦 Export folder as .mbox", FolderMenuAction.Export, folder.Selectable),
            Action("⚙️ Folder settings", FolderMenuAction.Settings),
            Action("👤 Account settings", FolderMenuAction.AccountSettings),
            Action("🌍 Global folder defaults", FolderMenuAction.GlobalSettings),
            Action("↩ Reset folder to global defaults", FolderMenuAction.ResetPreferences, folder.Role != FolderRole.Trash)
        ]);
        ContextMenuService.Open(this, FolderLabel(folder), items, pointer.ClientX, pointer.ClientY);
    }

    /// <summary>Changes only a notification deadline; no message flags or sync state are touched.</summary>
    /// <param name="folder">Captured folder.</param>
    /// <param name="minutes">Preset duration; null mutes indefinitely.</param>
    /// <param name="unmute">Explicitly enables notifications even when a default mutes Spam.</param>
    internal void SetFolderMute(MailFolderData folder, int? minutes = null, bool unmute = false)
    {
        if (folder.Role == FolderRole.Trash) return;
        var preferences = FolderSettingsStore.GetOverrides(folder.Role, folder.AccountId, folder.FullName) with
        {
            MutedUntilUtc = unmute ? DateTimeOffset.MinValue
                : minutes is { } duration ? DateTimeOffset.UtcNow.AddMinutes(duration) : DateTimeOffset.MaxValue
        };
        FolderActionStatus = FolderSettingsStore.Save(folder.Role, preferences, folder.AccountId, folder.FullName)
            ? unmute ? "Folder unmuted." : "Folder muted." : "Could not save folder settings. See the log for details.";
    }

    /// <summary>Dispatches an existing folder operation and reports its final UI failure.</summary>
    /// <param name="folder">Captured account/folder identity.</param>
    /// <param name="action">Selected command.</param>
    internal void RunFolderMenuAction(MailFolderData folder, FolderMenuAction action)
    {
        Log($"Folder action started: action={action}; account={folder.AccountId}; folder='{folder.FullName}'.", Run: IsFolderLogEnabled);
        try
        {
            FolderActionStatus = null;
            switch (action)
            {
                case FolderMenuAction.Open: MailUiState.SelectFolder(folder); break;
                case FolderMenuAction.Sync:
                case FolderMenuAction.Backfill:
                    if (AccountStore.GetById(folder.AccountId) is { } account)
                        ImapSyncService.KickFolderSync(account, folder.FullName,
                            backfillCount: action == FolderMenuAction.Backfill ? BackfillBatchSize : null);
                    break;
                case FolderMenuAction.Resync: ResyncFolder(folder); break;
                case FolderMenuAction.Export: ExportFolderMailbox(folder); break;
                case FolderMenuAction.SearchUnread:
                    MailUiState.SelectFolder(folder);
                    SearchAllFolders = false;
                    SearchText = "is:unread";
                    break;
                case FolderMenuAction.Settings:
                    Navigation.NavigateTo($"{AccountSettingsPage.RouteFor(folder.AccountId)}?folder={Uri.EscapeDataString(folder.FullName)}#folder-settings");
                    break;
                case FolderMenuAction.AccountSettings: Navigation.NavigateTo(AccountSettingsPage.RouteFor(folder.AccountId)); break;
                case FolderMenuAction.GlobalSettings: Navigation.NavigateTo($"{NavigationConstants.Settings.Link}#folder-settings"); break;
                case FolderMenuAction.ToggleUnreadBadge:
                    SaveFolderMenuPreferences(folder, FolderSettingsStore.GetOverrides(folder.Role, folder.AccountId, folder.FullName)
                        with { ShowUnreadBadge = !FolderSettingsStore.ShowsUnreadCount(folder) });
                    break;
                case FolderMenuAction.ResetPreferences: SaveFolderMenuPreferences(folder, null); break;
            }
            Log($"Folder action finished: action={action}; folder='{folder.FullName}'.", Run: IsFolderLogEnabled);
        }
        catch (Exception exception)
        {
            FolderActionStatus = $"Could not complete folder action: {exception.Message}";
            Log($"Folder action failed: action={action}; account={folder.AccountId}; folder='{folder.FullName}'; {exception}", LogLevel.Error, Run: IsFolderLogEnabled);
        }
    }

    /// <summary>Persists a menu preference and exposes failure beside the folders.</summary>
    /// <param name="folder">Captured folder scope.</param>
    /// <param name="preferences">New overrides, or null to inherit.</param>
    void SaveFolderMenuPreferences(MailFolderData folder, FolderPreferences? preferences)
    {
        FolderActionStatus = FolderSettingsStore.Save(folder.Role, preferences, folder.AccountId, folder.FullName)
            ? "Folder settings saved." : "Could not save folder settings. See the log for details.";
    }
}
