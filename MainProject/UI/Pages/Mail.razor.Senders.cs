using Microsoft.AspNetCore.Components.Web;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Services.Mail.FastMail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.UI.Pages;

public partial class Mail
{
    /// <summary>Captures the address and account, so a delayed menu action cannot affect another account.</summary>
    internal void OpenAddressMenu(string address, MouseEventArgs pointer, string? accountId = null)
    {
        accountId ??= MailUiState.SelectedAccount?.Id;
        if (accountId == null || string.IsNullOrWhiteSpace(address)) return;
        string trimmed = address.Trim();
        ContextMenuService.Open(
            this,
            trimmed,
            SenderMenuItems(accountId, trimmed),
            pointer.ClientX,
            pointer.ClientY,
            UiSurfaceIds.SenderContextMenu,
            id => ResolveMenuAddedItem(UiSurfaceIds.SenderContextMenu, id, senderAccountId: accountId, senderAddress: trimmed));
    }

    /// <summary>Opens the sender context menu from a Sender List row in Column 2.</summary>
    internal void OpenSenderRowMenu(SenderItem sender, MouseEventArgs pointer)
    {
        var account = MailUiState.SelectedAccount;
        if (account == null || string.IsNullOrWhiteSpace(sender.Address)) return;
        string trimmed = sender.Address.Trim();
        string title = string.Equals(sender.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{sender.DisplayName} <{trimmed}>";

        List<ContextMenuItem> items =
        [
            new("✉️ Show mails in folder", () => SelectSender(sender), actionId: "Sender.OpenFolderMail"),
            .. SenderMenuItems(account.Id, trimmed)
        ];

        ContextMenuService.Open(
            this,
            title,
            items,
            pointer.ClientX,
            pointer.ClientY,
            UiSurfaceIds.SenderContextMenu,
            id => ResolveMenuAddedItem(UiSurfaceIds.SenderContextMenu, id, senderAccountId: account.Id, senderAddress: trimmed));
    }

    internal IReadOnlyList<ContextMenuItem> SenderMenuItems(string accountId, string address)
    {
        var account = AccountStore.GetById(accountId);
        if (account == null) return [];
        var cached = SenderMessages(accountId, address);
        var profile = PersonProfileService.Build(accountId, address);
        string domain = address.Contains('@') ? address[(address.LastIndexOf('@') + 1)..] : string.Empty;

        ContextMenuItem Search(string label, string query, string actionId) =>
            new(label, () => SearchSender(accountId, query), actionId: actionId);

        ContextMenuItem Past(string label, MailMessageCommand command, string actionId, string? argument = null,
            Func<MailMessageSummary, bool>? predicate = null) =>
            new(label, () => RunSenderCommandAsync(accountId, address, command, argument, predicate), cached.Length > 0, ActionId: actionId);

        var destinations = SenderMoveTargets(account, cached);
        var notification = SenderRuleAction(accountId, address, FilterActionType.Mute, FilterActionType.Unmute);
        List<ContextMenuItem> items =
        [
            new("✏️ Write to this person", () => ComposeToSender(accountId, address), actionId: "Sender.Write"),
            new("👤 Person details", () => { SelectSenderAccount(accountId); MailUiState.OpenPerson(address); }, actionId: "Sender.OpenPerson"),
            new("🔔 Future notifications", Children:
            [
                new(notification == FilterActionType.Mute ? "✓ Mute this sender" : "Mute this sender", () => SetSenderNotifications(accountId, address, true), actionId: "Sender.NotifyMute"),
                new(notification == FilterActionType.Unmute ? "✓ Notify for this sender" : "Notify for this sender", () => SetSenderNotifications(accountId, address, false), actionId: "Sender.NotifyUnmute"),
                new("Use inherited notification settings", () => ResetSenderNotifications(accountId, address), actionId: "Sender.NotifyInherit")
            ], ActionId: "Sender.NotificationsGroup"),
            new("📂 Move past and future mail to", Enabled: destinations.Count > 0,
                Children: [.. destinations.Select(folder => new ContextMenuItem(FolderLabel(folder),
                    () => MoveSenderMailAsync(accountId, address, folder.FullName, includePast: true), ActionId: $"Sender.MovePastAndFuture:{folder.FullName}"))],
                ActionId: "Sender.MovePastFutureGroup"),
            new($"📚 Cached mail from this sender ({cached.Length})", Children:
            [
                Past("Mark all read", MailMessageCommand.Read, "Sender.MarkAllRead"),
                Past("Mark all unread", MailMessageCommand.Unread, "Sender.MarkAllUnread"),
                Past("Star all", MailMessageCommand.ToggleStar, "Sender.StarAll", predicate: message => !message.Flags.HasFlag(MailFlags.Flagged)),
                Past("Remove all stars", MailMessageCommand.ToggleStar, "Sender.UnstarAll", predicate: message => message.Flags.HasFlag(MailFlags.Flagged)),
                Past("Mark all important", MailMessageCommand.ToggleImportant, "Sender.ImportantAll", predicate: message => !message.Flags.HasFlag(MailFlags.Important)),
                Past("Clear importance", MailMessageCommand.ToggleImportant, "Sender.UnimportantAll", predicate: message => message.Flags.HasFlag(MailFlags.Important)),
                Past("Archive all", MailMessageCommand.Archive, "Sender.ArchiveAll"),
                Past("Move all to Trash", MailMessageCommand.Trash, "Sender.TrashAll"),
                Past("Move all to Spam", MailMessageCommand.Spam, "Sender.SpamAll"),
                Past("Restore all to Inbox", MailMessageCommand.Restore, "Sender.RestoreAll"),
                new("Move existing mail to", Enabled: destinations.Count > 0, Children: [.. destinations.Select(folder =>
                    Past(FolderLabel(folder), MailMessageCommand.Move, $"Sender.MoveAll:{folder.FullName}", folder.FullName))], ActionId: "Sender.MoveAllGroup"),
                new("Add tag to all", Enabled: TagStore.AllTags.Count > 0, Children: [.. TagStore.AllTags.Select(tag =>
                    Past($"🏷️ {tag}", MailMessageCommand.AddTag, $"Sender.TagAll:{tag}", tag))], ActionId: "Sender.TagAllGroup"),
                new("Remove tag from all", Enabled: profile.Tags.Count > 0, Children: [.. profile.Tags.Select(tag =>
                    Past(tag, MailMessageCommand.RemoveTag, $"Sender.RemoveTagAll:{tag}", tag))], ActionId: "Sender.RemoveTagAllGroup"),
                new("Snooze all", Enabled: cached.Length > 0, Children: [.. SnoozeService.Presets.Select(preset =>
                    new ContextMenuItem(preset.Label, () => RunSenderSnoozeAsync(accountId, address, preset.DueUtc()), ActionId: $"Sender.SnoozeAll:{preset.Label}"))], ActionId: "Sender.SnoozeAllGroup"),
                Past("Wake snoozed mail now", MailMessageCommand.Wake, "Sender.WakeAll", predicate: message => message.SnoozedUntilUtc != null),
                new("Select all cached mail from this sender", () => SelectSenderMessages(accountId, address), cached.Length > 0, actionId: "Sender.SelectAll"),
                new("Open newest received message", () => OpenSenderMessage(accountId, address, newest: true), cached.Length > 0, actionId: "Sender.OpenNewest"),
                new("Open oldest received message", () => OpenSenderMessage(accountId, address, newest: false), cached.Length > 0, actionId: "Sender.OpenOldest")
            ], ActionId: "Sender.CachedMailGroup"),
            new("🔎 Find", Children:
            [
                Search("Mail from this sender", "from:" + address, "Sender.FindFrom"),
                Search("Mail sent to this person", "to:" + address, "Sender.FindTo"),
                Search("Unread from this sender", "from:" + address + " is:unread", "Sender.FindUnread"),
                Search("Attachments from this sender", "from:" + address + " has:attachment", "Sender.FindAttachments"),
                Search("Starred from this sender", "from:" + address + " is:starred", "Sender.FindStarred"),
                Search("Important from this sender", "from:" + address + " is:important", "Sender.FindImportant"),
                Search("Recent mail from this sender", "from:" + address + " newer_than:7d", "Sender.FindRecent"),
                Search("Mail from this domain", "from:@" + domain, "Sender.FindDomain"),
                new("Save sender search", () => SaveSenderSearch(address), actionId: "Sender.SaveSearch")
            ], ActionId: "Sender.FindGroup"),
            new("⧉ Copy", Children:
            [
                new("Address", () => MouseActionService.RunAsync(MouseActionService.CopyAction, address), ActionId: "Sender.CopyAddress"),
                new("Display name", () => MouseActionService.RunAsync(MouseActionService.CopyAction, profile.DisplayName), profile.DisplayName.Length > 0, ActionId: "Sender.CopyName"),
                new("Name and address", () => MouseActionService.RunAsync(MouseActionService.CopyAction, new Contact(address, profile.DisplayName).Suggestion), ActionId: "Sender.CopyFull"),
                new("Domain", () => MouseActionService.RunAsync(MouseActionService.CopyAction, domain), domain.Length > 0, ActionId: "Sender.CopyDomain")
            ], ActionId: "Sender.CopyGroup"),
            new("⚙️ Future mail rules", Children: SenderFutureRuleItems(account, address), ActionId: "Sender.RulesGroup"),
        ];
        return items;
    }

    IReadOnlyList<ContextMenuItem> SenderFutureRuleItems(MailAccountData account, string address)
    {
        ContextMenuItem Set(string label, FilterActionType type, string actionId, string argument = "") => new(label, () =>
        {
            FilterRuleStore.UpsertSenderActionRule(account.Id, address, new FilterAction { Type = type, Argument = argument });
            MouseActionService.ReportStatus("Future mail rule saved for this sender in this account.");
        }, actionId: actionId);

        var folders = MessageStore.GetFolders(account.Id).Where(folder => folder.Selectable && folder.Role is not (FolderRole.Drafts or FolderRole.Outbox)
            && (folder.IsLocal || account.Protocol == IncomingProtocol.Imap)).ToList();
        return
        [
            new("Move future mail only to", Enabled: folders.Count > 0, Children: [.. folders.Select(folder =>
                new ContextMenuItem(FolderLabel(folder), () => MoveSenderMailAsync(account.Id, address, folder.FullName, includePast: false), ActionId: $"Sender.RuleMoveFuture:{folder.FullName}"))], ActionId: "Sender.RuleMoveFutureGroup"),
            new("Stop automatic filing", () => FilterRuleStore.RemoveSenderActionRule(account.Id, address, FilterActionType.MoveToRemoteFolder), actionId: "Sender.RuleStopMove"),
            Set("Mark future mail read", FilterActionType.MarkRead, "Sender.RuleMarkRead"),
            new("Stop automatic read marking", () => FilterRuleStore.RemoveSenderActionRule(account.Id, address, FilterActionType.MarkRead), actionId: "Sender.RuleStopMarkRead"),
            Set("Star future mail", FilterActionType.Flag, "Sender.RuleFlag"),
            new("Stop automatic stars", () => FilterRuleStore.RemoveSenderActionRule(account.Id, address, FilterActionType.Flag), actionId: "Sender.RuleStopFlag"),
            Set("Mark future mail important", FilterActionType.MarkImportant, "Sender.RuleImportant"),
            new("Stop automatic importance", () => FilterRuleStore.RemoveSenderActionRule(account.Id, address, FilterActionType.MarkImportant), actionId: "Sender.RuleStopImportant"),
            new("Tag future mail", Enabled: TagStore.AllTags.Count > 0, Children: [.. TagStore.AllTags.Select(tag => Set($"🏷️ {tag}", FilterActionType.AddTag, $"Sender.RuleTag:{tag}", tag))], ActionId: "Sender.RuleTagGroup"),
            new("Stop automatic tagging", () => FilterRuleStore.RemoveSenderActionRule(account.Id, address, FilterActionType.AddTag), actionId: "Sender.RuleStopTag"),
            Set("Silence future notification sounds", FilterActionType.SetNotificationSound, "Sender.RuleSilentSound", NotificationService.SilentSoundName),
            new("Use inherited notification sound", () => FilterRuleStore.RemoveSenderActionRule(account.Id, address, FilterActionType.SetNotificationSound), actionId: "Sender.RuleInheritSound"),
            new("Edit sender rules", () => Navigation.NavigateTo(FilterRules.LinkFor(accountId: account.Id, senderAddress: address)), actionId: "Sender.EditRules")
        ];
    }

    internal static MailMessageIdentity[] SenderMessages(string accountId, string address,
        Func<MailMessageSummary, bool>? predicate = null) =>
        [.. MessageStore.GetFolders(accountId).SelectMany(folder => MessageStore.GetSummaries(accountId, folder.FullName)
            .Where(message => message.FromAddress.Equals(address, StringComparison.OrdinalIgnoreCase)
                && (predicate == null || predicate(message)))
            .Select(message => new MailMessageIdentity(accountId, folder.FullName, message.Uid)))];

    static List<MailFolderData> SenderMoveTargets(MailAccountData account, MailMessageIdentity[] cached)
    {
        bool canMoveRemote = account.Protocol == IncomingProtocol.Imap && cached.All(key =>
            !MessageStore.IsLocalFolder(key.FolderFullName) && FolderSyncPolicy.PushesToServer(account.Id, key.FolderFullName));
        return [.. MessageStore.GetFolders(account.Id).Where(folder => folder.Selectable
            && folder.Role is not (FolderRole.Drafts or FolderRole.Outbox) && (folder.IsLocal || canMoveRemote))];
    }

    static FilterActionType? SenderRuleAction(string accountId, string address, params FilterActionType[] types) =>
        FilterRuleStore.Rules.Where(rule => rule.Enabled && rule.AccountId == accountId
            && rule.SenderAddress.Equals(address, StringComparison.OrdinalIgnoreCase)
            && rule.Conditions.Count == 0 && rule.AccountFolderId.Length == 0 && rule.FolderFullName.Length == 0 && rule.Actions.Count == 1)
            .SelectMany(rule => rule.Actions).FirstOrDefault(action => types.Contains(action.Type))?.Type;

    static void SelectSenderAccount(string accountId)
    {
        var account = AccountStore.GetById(accountId) ?? throw new InvalidOperationException("The account no longer exists.");
        if (MailUiState.SelectedAccount?.Id != accountId) MailUiState.SelectAccount(account, restoreFolder: true);
    }

    internal void SearchSender(string accountId, string query) { SelectSenderAccount(accountId); RunSavedSearch(query); }

    internal static void ComposeToSender(string accountId, string address)
    {
        var account = AccountStore.GetById(accountId) ?? throw new InvalidOperationException("The account no longer exists.");
        var draft = ComposeService.BuildNew(account);
        draft.To = address;
        MailUiState.OpenCompose(draft);
    }

    internal static void SetSenderNotifications(string accountId, string address, bool muted)
    {
        FilterRuleStore.UpsertSenderMuteRule(accountId, address, muted);
        MouseActionService.ReportStatus(muted ? "Future notifications muted for this sender in this account." : "Future notifications enabled for this sender in this account.");
    }

    internal static void ResetSenderNotifications(string accountId, string address)
    {
        FilterRuleStore.RemoveSenderActionRule(accountId, address, FilterActionType.Mute);
        MouseActionService.ReportStatus("This sender now inherits notification settings.");
    }

    internal async Task RunSenderCommandAsync(string accountId, string address, MailMessageCommand command, string? argument = null,
        Func<MailMessageSummary, bool>? predicate = null)
    {
        var keys = SenderMessages(accountId, address, predicate);
        if (command is MailMessageCommand.Move or MailMessageCommand.Archive or MailMessageCommand.Trash or MailMessageCommand.Spam or MailMessageCommand.Restore)
        {
            var account = AccountStore.GetById(accountId) ?? throw new InvalidOperationException("The account no longer exists.");
            var folders = MessageStore.GetFolders(accountId);
            foreach (var key in keys)
            {
                bool localDestination;
                if (command == MailMessageCommand.Move)
                    localDestination = folders.FirstOrDefault(folder => folder.FullName == argument)?.IsLocal == true;
                else
                {
                    var role = command switch
                    {
                        MailMessageCommand.Archive => FolderRole.Archive,
                        MailMessageCommand.Trash => FolderRole.Trash,
                        MailMessageCommand.Spam => FolderRole.Junk,
                        _ => FolderRole.Inbox
                    };
                    if (folders.Any(folder => folder.FullName == key.FolderFullName && folder.Role == role)) continue;
                    localDestination = account.Protocol != IncomingProtocol.Imap || MessageStore.IsLocalFolder(key.FolderFullName)
                        || !FolderSyncPolicy.PushesToServer(accountId, key.FolderFullName)
                        || !folders.Any(folder => folder.Role == role && !folder.IsLocal && folder.Selectable);
                }
                if (!localDestination || MessageStore.HasFullMessage(accountId, key.FolderFullName, key.Uid)) continue;
                MouseActionService.ReportStatus("Downloading sender message bodies before local filing.");
                using var message = account.Protocol switch
                {
                    IncomingProtocol.Imap when !MessageStore.IsLocalFolder(key.FolderFullName)
                        => await ImapSyncService.DownloadMessageAsync(account, key.FolderFullName, key.Uid),
                    IncomingProtocol.Pop3 => await Pop3Service.DownloadMessageAsync(account, key.Uid),
                    IncomingProtocol.FastMail => await FastMailSyncService.DownloadMessageAsync(account, key.Uid),
                    _ => throw new InvalidOperationException("A local message body is missing. Sender filing stopped before moving messages.")
                };
                if (!MessageStore.HasFullMessage(accountId, key.FolderFullName, key.Uid))
                {
                    using var buffer = new MemoryStream();
                    await message.WriteToAsync(buffer);
                    MessageStore.SaveFullMessage(accountId, key.FolderFullName, key.Uid, buffer.ToArray());
                }
            }
        }
        await MouseActionService.RunMessagesAsync(command, keys, argument);
    }

    internal Task RunSenderSnoozeAsync(string accountId, string address, DateTime dueUtc) =>
        MouseActionService.RunMessagesAsync(MailMessageCommand.Snooze, SenderMessages(accountId, address), dueUtc: dueUtc);

    internal async Task MoveSenderMailAsync(string accountId, string address, string folderFullName, bool includePast)
    {
        var account = AccountStore.GetById(accountId) ?? throw new InvalidOperationException("The account no longer exists.");
        var keys = SenderMessages(accountId, address);
        var folder = MessageStore.GetFolders(accountId).FirstOrDefault(folder => folder.FullName == folderFullName && folder.Selectable)
            ?? throw new InvalidOperationException("The destination folder no longer exists in this account.");
        if (includePast && !SenderMoveTargets(account, keys).Any(target => target.FullName == folder.FullName))
            throw new InvalidOperationException("Choose a local folder to include previously filed local messages.");
        if (includePast)
        {
            MouseActionService.ReportStatus("Loading this sender's retained history before filing; existing mail stays in place until loading completes.");
            switch (account.Protocol)
            {
                case IncomingProtocol.Imap:
                    await ImapSyncService.CacheSenderHistoryAsync(account, address, folder.IsLocal, folder.FullName);
                    break;
                case IncomingProtocol.Pop3:
                    await Pop3Service.CacheSenderHistoryAsync(account, address);
                    break;
                case IncomingProtocol.FastMail:
                    await FastMailSyncService.CacheSenderHistoryAsync(account, address);
                    break;
            }
            if (AccountStore.GetById(accountId) == null) throw new InvalidOperationException("The account no longer exists.");
            keys = SenderMessages(accountId, address);
            if (!SenderMoveTargets(account, keys).Any(target => target.FullName == folder.FullName))
                throw new InvalidOperationException("The loaded history includes local-only mail. Choose a local destination to include it.");
            if (folder.IsLocal && keys.Where(key => key.FolderFullName != folder.FullName)
                .Any(key => !MessageStore.HasFullMessage(accountId, key.FolderFullName, key.Uid)))
                throw new InvalidOperationException("A past message's full body could not be recovered. No sender mail was moved.");
        }
        FilterRuleStore.UpsertSenderMoveRule(accountId, address, folder.FullName, folder.IsLocal);
        string pastResult = includePast
            ? await MouseActionService.RunMessagesAsync(MailMessageCommand.Move, keys, folder.FullName)
            : string.Empty;
        MouseActionService.ReportStatus("Future sender filing rule saved in this account." + (includePast ? " " + pastResult : string.Empty));
    }

    internal void SelectSenderMessages(string accountId, string address)
    {
        SearchSender(accountId, "from:" + address);
        MailUiState.SelectMessages(SenderMessages(accountId, address), replace: true);
    }

    internal void OpenSenderMessage(string accountId, string address, bool newest)
    {
        var keys = SenderMessages(accountId, address).OrderBy(key => MessageStore.GetSummary(accountId, key.FolderFullName, key.Uid)?.DateUtc);
        var snapshot = keys.ToArray();
        if (snapshot.Length > 0) OpenMessage(ResolveMenuMessage(newest ? snapshot[^1] : snapshot[0]));
    }

    internal static void SaveSenderSearch(string address)
    {
        string query = "from:" + address;
        var saved = SavedSearchList;
        if (!saved.Contains(query)) { saved.Add(query); PersistSavedSearches(saved); }
        MouseActionService.ReportStatus("Sender search saved.");
    }
}
