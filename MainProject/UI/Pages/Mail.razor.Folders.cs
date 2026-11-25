using Microsoft.AspNetCore.Components.Web;
using MyLovelyMail.MainProject.Constants;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.UI.Components;

namespace MyLovelyMail.MainProject.UI.Pages;

public partial class Mail
{
    const bool IsFolderLogEnabled = true;

    readonly HashSet<string> busyDeletedFolderIds = new(StringComparer.OrdinalIgnoreCase);

    internal bool IsDeletedFolderBusy(string entryId) => busyDeletedFolderIds.Contains(entryId);

    internal IReadOnlyList<DeletedFolderEntry> GetVisibleDeletedFolderEntries()
    {
        IReadOnlyList<DeletedFolderEntry> source = MailUiState.DeletedFoldersAccountFilter is { Length: > 0 } accId
            ? DeletedFolderStore.GetForAccount(accId)
            : DeletedFolderStore.GetAccountGroups();

        if (string.IsNullOrWhiteSpace(SearchText))
            return source;

        string filter = SearchText.Trim();
        return [.. source.Where(e =>
            e.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || e.FullPathLabel.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || e.AccountEmail.Contains(filter, StringComparison.OrdinalIgnoreCase))];
    }

    internal async Task DeleteMailFolderWithConfirmationAsync(MailAccountData account, MailFolderData folder)
    {
        if (!MailFolderManagementService.CanDelete(folder))
        {
            MouseActionService.ReportStatus($"'{folder.DisplayName}' is a required system folder and cannot be deleted.");
            return;
        }

        int count = MessageStore.CountSummaries(account.Id, folder.FullName);
        var (autoPurge, days) = TrashRetentionService.ResolveDeletedFoldersPolicy(account.Id);
        string messageNote = count > 0
            ? $" ({count:N0} cached message(s) will be archived with it)"
            : string.Empty;
        string retentionLine = autoPurge
            ? $"It will move to Deleted Folders Trash and be permanently deleted after {days} day(s)."
            : "It will move to Deleted Folders Trash (auto-delete is currently off).";

        bool confirmed = await ModalService.ConfirmAsync(
            $"Delete folder '{folder.DisplayName}'?",
            $"Are you sure you want to delete '{folder.DisplayName}'{messageNote} from {account.EmailAddress}?\n\n{retentionLine}",
            confirmLabel: "🗑️ Move to Folder Trash",
            cancelLabel: "Cancel",
            isDanger: true);

        if (!confirmed) return;

        FolderActionStatus = $"Moving '{folder.DisplayName}' to Deleted Folders Trash…";
        StateHasChanged();
        try
        {
            await MailFolderManagementService.MoveMailFolderToTrashAsync(account, folder);
            FolderActionStatus = $"Moved '{folder.DisplayName}' to Deleted Folders Trash.";
            MouseActionService.ReportStatus(FolderActionStatus);
            RefreshSidebarFolders();
        }
        catch (Exception ex)
        {
            FolderActionStatus = null;
            Log($"Delete folder '{folder.FullName}' failed: {ex.Message}", LogLevel.Error, Run: IsFolderLogEnabled);
            await ModalService.AlertAsync("Could not delete folder", ex.Message);
        }
        finally
        {
            StateHasChanged();
        }
    }

    internal async Task RestoreDeletedFolderEntryAsync(DeletedFolderEntry entry)
    {
        if (!busyDeletedFolderIds.Add(entry.Id)) return;
        FolderActionStatus = $"Restoring '{entry.DisplayName}'…";
        StateHasChanged();
        try
        {
            await MailFolderManagementService.RestoreDeletedEntryAsync(entry);
            FolderActionStatus = $"Restored folder '{entry.DisplayName}'.";
            MouseActionService.ReportStatus(FolderActionStatus);
            RefreshSidebarFolders();
        }
        catch (Exception ex)
        {
            FolderActionStatus = null;
            Log($"Restore deleted folder '{entry.DisplayName}' failed: {ex.Message}", LogLevel.Error, Run: IsFolderLogEnabled);
            await ModalService.AlertAsync("Could not restore folder", ex.Message);
        }
        finally
        {
            busyDeletedFolderIds.Remove(entry.Id);
            StateHasChanged();
        }
    }

    internal async Task PurgeDeletedFolderEntryWithConfirmationAsync(DeletedFolderEntry entry)
    {
        bool confirmed = await ModalService.ConfirmAsync(
            $"Delete '{entry.DisplayName}' forever?",
            $"Are you sure you want to permanently delete '{entry.DisplayName}' from Deleted Folders Trash? Its archived contents cannot be recovered.",
            confirmLabel: "🗑️ Delete forever",
            cancelLabel: "Cancel",
            isDanger: true);

        if (!confirmed) return;

        DeletedFolderStore.RemovePermanently(entry.Id);
        MouseActionService.ReportStatus($"Permanently deleted folder '{entry.DisplayName}'.");
    }

    internal async Task EmptyDeletedFoldersWithConfirmationAsync(string? accountIdFilter)
    {
        var items = accountIdFilter is { Length: > 0 }
            ? DeletedFolderStore.GetForAccount(accountIdFilter)
            : DeletedFolderStore.GetAccountGroups();

        if (items.Count == 0)
        {
            MouseActionService.ReportStatus("Deleted Folders Trash is already empty.");
            return;
        }

        bool confirmed = await ModalService.ConfirmAsync(
            "Empty Folder Trash permanently?",
            $"Are you sure you want to permanently delete all {items.Count:N0} folder(s) in Deleted Folders Trash? This action cannot be undone.",
            confirmLabel: $"🗑️ Delete {items.Count:N0} folder(s) forever",
            cancelLabel: "Cancel",
            isDanger: true);

        if (!confirmed) return;

        var kind = accountIdFilter is { Length: > 0 } ? DeletedFolderKind.MailFolder : DeletedFolderKind.AccountGroup;
        int removed = DeletedFolderStore.EmptyAll(accountIdFilter, kind);
        MouseActionService.ReportStatus($"Permanently deleted {removed:N0} folder(s) from Folder Trash.");
    }

    internal void OpenDeletedFoldersMenu(string? accountIdFilter, MouseEventArgs pointer)
    {
        int count = accountIdFilter is { Length: > 0 }
            ? DeletedFolderStore.GetForAccount(accountIdFilter).Count
            : DeletedFolderStore.GetAccountGroups().Count;

        List<ContextMenuItem> items =
        [
            new("📂 Open Folder Trash", () => MailUiState.SelectDeletedFoldersBin(accountIdFilter)),
            new($"⏱️ Auto-delete settings ({TrashRetentionService.BuildDeletedFoldersBadge(accountIdFilter)})…",
                () => ModalService.OpenDeletedFoldersRetention(accountIdFilter)),
            new("🧹 Empty Folder Trash permanently…",
                () => EmptyDeletedFoldersWithConfirmationAsync(accountIdFilter),
                Enabled: count > 0)
        ];

        ContextMenuService.Open(this, "Deleted Folders Trash", items, pointer.ClientX, pointer.ClientY);
    }

    internal enum FolderMenuAction
    {
        Open, Sync, Backfill, Resync, Export, SearchUnread,
        Settings, Filters, AccountSettings, GlobalSettings, ToggleUnreadBadge, ResetPreferences
    }

    internal async Task EmptyFolderWithConfirmationAsync(MailAccountData account, MailFolderData folder)
    {
        int count = MessageStore.CountSummaries(account.Id, folder.FullName);
        if (count <= 0)
        {
            MouseActionService.ReportStatus($"'{folder.DisplayName}' is already empty.");
            return;
        }

        string label = folder.Role == FolderRole.Trash ? "Trash" : folder.DisplayName;
        bool confirmed = await ModalService.ConfirmAsync(
            $"Empty {label} permanently?",
            $"This will permanently delete all {count:N0} message(s) in '{folder.DisplayName}' ({account.EmailAddress}) from disk and server. This action cannot be undone.",
            confirmLabel: $"🗑️ Delete {count:N0} message(s) forever",
            cancelLabel: "Cancel",
            isDanger: true);

        if (!confirmed) return;

        int removed = MessageActions.EmptyFolder(account, folder.FullName);
        MouseActionService.ReportStatus($"Permanently deleted {removed:N0} message(s) from '{folder.DisplayName}'.");
    }

    internal IReadOnlyList<ContextMenuItem> BuildFolderMenuItems(MailFolderData folder)
    {
        var account = AccountStore.GetById(folder.AccountId);
        bool remote = folder.Selectable && !folder.IsLocal && account?.Protocol == IncomingProtocol.Imap;
        int cachedCount = MessageStore.CountSummaries(folder.AccountId, folder.FullName);

        ContextMenuItem Action(string label, FolderMenuAction action, string actionId, bool enabled = true, bool separatorBefore = false) =>
            new(label, () => RunFolderMenuAction(folder, action), enabled, actionId: actionId, separatorBefore: separatorBefore);

        List<ContextMenuItem> items = [Action("📂 Open folder", FolderMenuAction.Open, "Folder.Open", folder.Selectable)];

        if (account != null)
        {
            items.Add(new("📁+ New mail folder…",
                () => ModalService.OpenCreateMailFolder(folder.AccountId, folder.IsLocal ? null : folder.FullName),
                actionId: "Folder.NewSubfolder"));

            if (MailFolderManagementService.CanRename(folder))
            {
                items.Add(new("✏️ Rename folder…",
                    () => ModalService.OpenRenameMailFolder(folder.AccountId, folder.FullName),
                    actionId: "Folder.Rename"));
            }

            if (MailFolderManagementService.CanDelete(folder))
            {
                items.Add(new("🗑️ Delete folder…",
                    () => DeleteMailFolderWithConfirmationAsync(account, folder),
                    ActionId: "Folder.Delete"));
            }
        }

        if (folder.Role == FolderRole.Trash)
        {
            items.Add(new($"⏱️ Trash auto-delete ({TrashRetentionService.BuildSidebarTrashBadge(folder)})…",
                () => ModalService.OpenMailTrashRetention(folder.AccountId, folder.FullName),
                actionId: "Folder.TrashRetention",
                separatorBefore: true));
        }

        if (folder.Role is FolderRole.Trash or FolderRole.Junk && account != null)
        {
            items.Add(new(
                folder.Role == FolderRole.Trash ? "🗑️ Empty Trash permanently…" : "🧹 Empty Spam permanently…",
                () => EmptyFolderWithConfirmationAsync(account, folder),
                Enabled: cachedCount > 0,
                ActionId: "Folder.Empty"));
        }

        if (folder.Role != FolderRole.Trash)
        {
            bool muted = FolderSettingsStore.IsMuted(folder);
            List<ContextMenuItem> durations = [.. FolderSettingsPanel.MutePeriods.Select(period =>
                new ContextMenuItem($"Mute for {period.Label}", () => SetFolderMute(folder, period.Minutes), actionId: $"Folder.MuteMinutes:{period.Minutes}"))];
            durations.Add(new("Mute forever", () => SetFolderMute(folder), actionId: "Folder.MuteForever"));
            items.Add(new(muted ? "🔔 Unmute notifications" : "🔕 Mute notifications", () => { SetFolderMute(folder, unmute: muted); return Task.CompletedTask; }, Children: durations, ActionId: "Folder.MuteGroup", SeparatorBefore: true));
            if (muted)
                items.Add(new(FolderSettingsPanel.MuteStatus(FolderSettingsStore.Resolve(folder.Role, folder.AccountId, folder.FullName)), Enabled: false, ActionId: "Folder.MuteStatus"));
            items.Add(Action(FolderSettingsStore.ShowsUnreadCount(folder) ? "Hide unread badges" : "Show unread badges", FolderMenuAction.ToggleUnreadBadge, "Folder.ToggleBadge"));
        }
        items.AddRange([
            Action("🔄 Check for new mail", FolderMenuAction.Sync, "Folder.Sync", remote, separatorBefore: true),
            Action("⬇️ Download older mail", FolderMenuAction.Backfill, "Folder.Backfill", remote),
            Action("♻️ Redownload folder", FolderMenuAction.Resync, "Folder.Resync", remote),
            new("✓✓ Mark all cached mail read", () => MouseActionService.ReadFolderAsync(folder), folder.Selectable, ActionId: "Folder.ReadAll"),
            Action("🔎 Find unread mail", FolderMenuAction.SearchUnread, "Folder.SearchUnread", folder.Selectable),
            Action("📦 Export folder as .mbox", FolderMenuAction.Export, "Folder.Export", folder.Selectable, separatorBefore: true),
            Action("🪄 Filters for this folder", FolderMenuAction.Filters, "Folder.Filters", folder.Selectable),
            Action("⚙️ Folder settings", FolderMenuAction.Settings, "Folder.Settings", separatorBefore: true),
            Action("👤 Account settings", FolderMenuAction.AccountSettings, "Folder.AccountSettings"),
            Action("🌐 Global folder defaults", FolderMenuAction.GlobalSettings, "Folder.GlobalSettings"),
            Action("↺ Reset folder to global defaults", FolderMenuAction.ResetPreferences, "Folder.ResetPrefs", folder.Role != FolderRole.Trash)
        ]);
        return items;
    }

    internal void OpenFolderMenu(MailFolderData folder, MouseEventArgs pointer) =>
        ContextMenuService.Open(
            this,
            FolderLabel(folder),
            BuildFolderMenuItems(folder),
            pointer.ClientX,
            pointer.ClientY,
            UiSurfaceIds.FolderContextMenu,
            id => ResolveMenuAddedItem(UiSurfaceIds.FolderContextMenu, id, folderContext: folder));

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
                            backfillCount: action == FolderMenuAction.Backfill ? Math.Max(1, BackfillBatchSize) : null);
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
                case FolderMenuAction.Filters: Navigation.NavigateTo(FilterRules.LinkFor(folder.AccountId, folder.FullName)); break;
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

    void SaveFolderMenuPreferences(MailFolderData folder, FolderPreferences? preferences)
    {
        FolderActionStatus = FolderSettingsStore.Save(folder.Role, preferences, folder.AccountId, folder.FullName)
            ? "Folder settings saved." : "Could not save folder settings. See the log for details.";
    }
}
