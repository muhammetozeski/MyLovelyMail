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
        ContextMenuService.Open(this, address, SenderMenuItems(accountId, address.Trim()), pointer.ClientX, pointer.ClientY);
    }

    IReadOnlyList<ContextMenuItem> SenderMenuItems(string accountId, string address)
    {
        var account = AccountStore.GetById(accountId);
        if (account == null) return [];
        var cached = SenderMessages(accountId, address);
        var profile = PersonProfileService.Build(accountId, address);
        string domain = address.Contains('@') ? address[(address.LastIndexOf('@') + 1)..] : string.Empty;
        ContextMenuItem Search(string label, string query) => new(label, () => SearchSender(accountId, query));
        ContextMenuItem Past(string label, MailMessageCommand command, string? argument = null,
            Func<MailMessageSummary, bool>? predicate = null) => new(label,
                () => RunSenderCommandAsync(accountId, address, command, argument, predicate), cached.Length > 0);
        var destinations = SenderMoveTargets(account, cached);
        var notification = SenderRuleAction(accountId, address, FilterActionType.Mute, FilterActionType.Unmute);
        List<ContextMenuItem> items =
        [
            new("✏️ Write to this person", () => ComposeToSender(accountId, address)),
            new("👤 Person details", () => { SelectSenderAccount(accountId); MailUiState.OpenPerson(address); }),
            new("🔔 Future notifications", Children:
            [
                new(notification == FilterActionType.Mute ? "✓ Mute this sender" : "Mute this sender", () => SetSenderNotifications(accountId, address, true)),
                new(notification == FilterActionType.Unmute ? "✓ Notify for this sender" : "Notify for this sender", () => SetSenderNotifications(accountId, address, false)),
                new("Use inherited notification settings", () => ResetSenderNotifications(accountId, address))
            ]),
            new("📂 Move past and future mail to", Enabled: destinations.Count > 0,
                Children: [.. destinations.Select(folder => new ContextMenuItem(FolderLabel(folder),
                    () => MoveSenderMailAsync(accountId, address, folder.FullName, includePast: true)))]),
            new($"📚 Cached mail from this sender ({cached.Length})", Children:
            [
                Past("Mark all read", MailMessageCommand.Read),
                Past("Mark all unread", MailMessageCommand.Unread),
                Past("Star all", MailMessageCommand.ToggleStar, predicate: message => !message.Flags.HasFlag(MailFlags.Flagged)),
                Past("Remove all stars", MailMessageCommand.ToggleStar, predicate: message => message.Flags.HasFlag(MailFlags.Flagged)),
                Past("Mark all important", MailMessageCommand.ToggleImportant, predicate: message => !message.Flags.HasFlag(MailFlags.Important)),
                Past("Clear importance", MailMessageCommand.ToggleImportant, predicate: message => message.Flags.HasFlag(MailFlags.Important)),
                Past("Archive all", MailMessageCommand.Archive),
                Past("Move all to Trash", MailMessageCommand.Trash),
                Past("Move all to Spam", MailMessageCommand.Spam),
                Past("Restore all to Inbox", MailMessageCommand.Restore),
                new("Move existing mail to", Enabled: destinations.Count > 0, Children: [.. destinations.Select(folder =>
                    Past(FolderLabel(folder), MailMessageCommand.Move, folder.FullName))]),
                new("Add tag to all", Enabled: TagStore.AllTags.Count > 0, Children: [.. TagStore.AllTags.Select(tag => Past(tag, MailMessageCommand.AddTag, tag))]),
                new("Remove tag from all", Enabled: profile.Tags.Count > 0, Children: [.. profile.Tags.Select(tag => Past(tag, MailMessageCommand.RemoveTag, tag))]),
                new("Snooze all", Enabled: cached.Length > 0, Children: [.. SnoozeService.Presets.Select(preset =>
                    new ContextMenuItem(preset.Label, () => RunSenderSnoozeAsync(accountId, address, preset.DueUtc())))]),
                Past("Wake snoozed mail now", MailMessageCommand.Wake, predicate: message => message.SnoozedUntilUtc != null),
                new("Select all cached mail from this sender", () => SelectSenderMessages(accountId, address), cached.Length > 0),
                new("Open newest received message", () => OpenSenderMessage(accountId, address, newest: true), cached.Length > 0),
                new("Open oldest received message", () => OpenSenderMessage(accountId, address, newest: false), cached.Length > 0)
            ]),
            new("🔎 Find", Children:
            [
                Search("Mail from this sender", "from:" + address),
                Search("Mail sent to this person", "to:" + address),
                Search("Unread from this sender", "from:" + address + " is:unread"),
                Search("Attachments from this sender", "from:" + address + " has:attachment"),
                Search("Starred from this sender", "from:" + address + " is:starred"),
                Search("Important from this sender", "from:" + address + " is:important"),
                Search("Recent mail from this sender", "from:" + address + " newer_than:7d"),
                Search("Mail from this domain", "from:@" + domain),
                new("Save sender search", () => SaveSenderSearch(address))
            ]),
            new("⧉ Copy", Children:
            [
                new("Address", () => MouseActionService.RunAsync(MouseActionService.CopyAction, address)),
                new("Display name", () => MouseActionService.RunAsync(MouseActionService.CopyAction, profile.DisplayName), profile.DisplayName.Length > 0),
                new("Name and address", () => MouseActionService.RunAsync(MouseActionService.CopyAction, new Contact(address, profile.DisplayName).Suggestion)),
                new("Domain", () => MouseActionService.RunAsync(MouseActionService.CopyAction, domain), domain.Length > 0)
            ]),
            new("⚙️ Future mail rules", Children: SenderFutureRuleItems(account, address)),
        ];
        return items;
    }

    IReadOnlyList<ContextMenuItem> SenderFutureRuleItems(MailAccountData account, string address)
    {
        ContextMenuItem Set(string label, FilterActionType type, string argument = "") => new(label, () =>
        {
            FilterRuleStore.UpsertSenderActionRule(account.Id, address, new FilterAction { Type = type, Argument = argument });
            MouseActionService.ReportStatus("Future mail rule saved for this sender in this account.");
        });
        var folders = MessageStore.GetFolders(account.Id).Where(folder => folder.Selectable && folder.Role is not (FolderRole.Drafts or FolderRole.Outbox)
            && (folder.IsLocal || account.Protocol == IncomingProtocol.Imap)).ToList();
        return
        [
            new("Move future mail only to", Enabled: folders.Count > 0, Children: [.. folders.Select(folder =>
                new ContextMenuItem(FolderLabel(folder), () => MoveSenderMailAsync(account.Id, address, folder.FullName, includePast: false)))]),
            new("Stop automatic filing", () => FilterRuleStore.RemoveSenderActionRule(account.Id, address, FilterActionType.MoveToRemoteFolder)),
            Set("Mark future mail read", FilterActionType.MarkRead),
            new("Stop automatic read marking", () => FilterRuleStore.RemoveSenderActionRule(account.Id, address, FilterActionType.MarkRead)),
            Set("Star future mail", FilterActionType.Flag),
            new("Stop automatic stars", () => FilterRuleStore.RemoveSenderActionRule(account.Id, address, FilterActionType.Flag)),
            Set("Mark future mail important", FilterActionType.MarkImportant),
            new("Stop automatic importance", () => FilterRuleStore.RemoveSenderActionRule(account.Id, address, FilterActionType.MarkImportant)),
            new("Tag future mail", Enabled: TagStore.AllTags.Count > 0, Children: [.. TagStore.AllTags.Select(tag => Set(tag, FilterActionType.AddTag, tag))]),
            new("Stop automatic tagging", () => FilterRuleStore.RemoveSenderActionRule(account.Id, address, FilterActionType.AddTag)),
            Set("Silence future notification sounds", FilterActionType.SetNotificationSound, NotificationService.SilentSoundName),
            new("Use inherited notification sound", () => FilterRuleStore.RemoveSenderActionRule(account.Id, address, FilterActionType.SetNotificationSound)),
            new("Edit sender rules", () => Navigation.NavigateTo(FilterRules.LinkFor(accountId: account.Id, senderAddress: address)))
        ];
    }

    static MailMessageIdentity[] SenderMessages(string accountId, string address,
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

    void SearchSender(string accountId, string query) { SelectSenderAccount(accountId); RunSavedSearch(query); }

    static void ComposeToSender(string accountId, string address)
    {
        var account = AccountStore.GetById(accountId) ?? throw new InvalidOperationException("The account no longer exists.");
        var draft = ComposeService.BuildNew(account);
        draft.To = address;
        MailUiState.OpenCompose(draft);
    }

    static void SetSenderNotifications(string accountId, string address, bool muted)
    {
        FilterRuleStore.UpsertSenderMuteRule(accountId, address, muted);
        MouseActionService.ReportStatus(muted ? "Future notifications muted for this sender in this account." : "Future notifications enabled for this sender in this account.");
    }

    static void ResetSenderNotifications(string accountId, string address)
    {
        FilterRuleStore.RemoveSenderActionRule(accountId, address, FilterActionType.Mute);
        MouseActionService.ReportStatus("This sender now inherits notification settings.");
    }

    async Task RunSenderCommandAsync(string accountId, string address, MailMessageCommand command, string? argument = null,
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

    Task RunSenderSnoozeAsync(string accountId, string address, DateTime dueUtc) =>
        MouseActionService.RunMessagesAsync(MailMessageCommand.Snooze, SenderMessages(accountId, address), dueUtc: dueUtc);

    async Task MoveSenderMailAsync(string accountId, string address, string folderFullName, bool includePast)
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

    void SelectSenderMessages(string accountId, string address)
    {
        SearchSender(accountId, "from:" + address);
        MailUiState.SelectMessages(SenderMessages(accountId, address), replace: true);
    }

    void OpenSenderMessage(string accountId, string address, bool newest)
    {
        var keys = SenderMessages(accountId, address).OrderBy(key => MessageStore.GetSummary(accountId, key.FolderFullName, key.Uid)?.DateUtc);
        var snapshot = keys.ToArray();
        if (snapshot.Length > 0) OpenMessage(ResolveMenuMessage(newest ? snapshot[^1] : snapshot[0]));
    }

    static void SaveSenderSearch(string address)
    {
        string query = "from:" + address;
        var saved = SavedSearchList;
        if (!saved.Contains(query)) { saved.Add(query); PersistSavedSearches(saved); }
        MouseActionService.ReportStatus("Sender search saved.");
    }
}
