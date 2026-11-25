using Microsoft.AspNetCore.Components.Web;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.UI.Pages;

public partial class Mail
{
    // Predicates choose rows, so a collapsed conversation is always selected as a whole.
    static readonly (string Id, string Label, Func<MailMessageSummary, bool> Matches)[] SelectionChoices =
    [
        ("Select.Unread", "Unread", static message => message.IsUnread),
        ("Select.Read", "Read", static message => !message.IsUnread),
        ("Select.Starred", "Starred", static message => message.Flags.HasFlag(MailFlags.Flagged)),
        ("Select.NotStarred", "Not starred", static message => !message.Flags.HasFlag(MailFlags.Flagged)),
        ("Select.Important", "Important", static message => message.Flags.HasFlag(MailFlags.Important)),
        ("Select.NotImportant", "Not important", static message => !message.Flags.HasFlag(MailFlags.Important)),
        ("Select.Attachments", "With attachments", static message => message.HasAttachments),
        ("Select.NoAttachments", "Without attachments", static message => !message.HasAttachments),
        ("Select.Tagged", "Tagged", static message => message.Tags.Count > 0),
        ("Select.Untagged", "Untagged", static message => message.Tags.Count == 0),
        ("Select.Muted", "Muted", static message => message.Flags.HasFlag(MailFlags.Muted)),
        ("Select.NotMuted", "Not muted", static message => !message.Flags.HasFlag(MailFlags.Muted)),
        ("Select.Snoozed", "Snoozed", static message => message.SnoozedUntilUtc != null)
    ];

    internal void OpenSelectionMenu(MouseEventArgs pointer) =>
        ContextMenuService.Open(this, "Select listed messages", SelectionMenuItems(), pointer.ClientX, pointer.ClientY);

    IReadOnlyList<ContextMenuItem> SelectionMenuItems()
    {
        var all = MessageIdentities(Rows.SelectMany(static row => row.Messages));
        var current = MailUiState.SelectedMessages.ToHashSet();
        var inverted = MessageIdentities(Rows.Where(row => !MessageTargets(row.Message).All(current.Contains))
            .SelectMany(static row => row.Messages));
        int focus = FocusedRowIndex();
        List<ContextMenuItem> choices =
        [
            new(SelectionMode ? "✓ Exit selection mode" : "☑ Toggle selection mode", () => SelectionMode = !SelectionMode, actionId: "List.SelectMode"),
            new("All listed messages", () => ApplyListedSelection(all), all.Length > 0, actionId: "List.SelectAll", separatorBefore: true),
            new("Clear selection", MailUiState.ClearSelection, current.Count > 0, actionId: "List.ClearSelection"),
            new("Invert listed selection", () => ApplyListedSelection(inverted), all.Length > 0, actionId: "Select.Invert")
        ];
        bool firstChoice = true;
        foreach (var choice in SelectionChoices)
        {
            var matching = MessageIdentities(Rows.Where(row => row.Messages.Any(choice.Matches))
                .SelectMany(static row => row.Messages));
            choices.Add(new($"{choice.Label} · {matching.Length}", () => ApplyListedSelection(matching), matching.Length > 0, actionId: choice.Id, separatorBefore: firstChoice));
            firstChoice = false;
        }
        var focused = focus < 0 ? [] : MessageTargets(Rows[focus].Message);
        var before = focus < 0 ? [] : MessageIdentities(Rows.Take(focus + 1).SelectMany(static row => row.Messages));
        var after = focus < 0 ? [] : MessageIdentities(Rows.Skip(focus).SelectMany(static row => row.Messages));
        choices.Add(new("Focused conversation only", () => ApplyListedSelection(focused), focus >= 0, actionId: "Select.Focused", separatorBefore: true));
        choices.Add(new("Start through focused row", () => ApplyListedSelection(before), focus >= 0, actionId: "Select.ThroughFocused"));
        choices.Add(new("Focused row through end", () => ApplyListedSelection(after), focus >= 0, actionId: "Select.FromFocused"));
        return choices;
    }

    void ApplyListedSelection(IEnumerable<MailMessageIdentity> captured)
    {
        var listed = MessageIdentities(Rows.SelectMany(static row => row.Messages)).ToHashSet();
        MailUiState.SelectMessages(captured.Where(listed.Contains), replace: true);
    }

    void InvertListedSelection()
    {
        var selected = MailUiState.SelectedMessages.ToHashSet();
        ApplyListedSelection(MessageIdentities(Rows.Where(row => !MessageTargets(row.Message).All(selected.Contains))
            .SelectMany(static row => row.Messages)));
    }

    int FocusedRowIndex() => MailUiState.FocusedMessageKey is { } key
        ? Rows.FindIndex(row => row.Messages.Any(message => MessageIdentityOf(message) == key)) : -1;

    void FocusListBoundary(bool last)
    {
        if (Rows.Count == 0) return;
        var message = Rows[last ? Rows.Count - 1 : 0].Message;
        MailUiState.FocusMessage(message, MessageIdentityOf(message));
    }

    void ExtendFocusedSelection(int delta)
    {
        int index = FocusedRowIndex();
        if (Rows.Count == 0) return;
        if (SelectionAnchor == null && index >= 0) SelectionAnchor = MessageIdentityOf(Rows[index].Message);
        int next = index < 0 ? 0 : Math.Clamp(index + delta, 0, Rows.Count - 1);
        var message = Rows[next].Message;
        if (SelectionAnchor == null) SelectionAnchor = MessageIdentityOf(message);
        MailUiState.FocusMessage(message, MessageIdentityOf(message));
        ToggleRowSelection(new MouseEventArgs { ShiftKey = true }, message);
    }

    internal MailMessageSummary? ListedNavigationTarget(int delta, bool unreadOnly = false, bool starredOnly = false)
    {
        if (Rows.Count == 0) return null;
        var key = MailUiState.OpenMessageKey ?? MailUiState.FocusedMessageKey;
        int index = key == null ? -1 : Rows.FindIndex(row => row.Messages.Any(message => MessageIdentityOf(message) == key));
        int start = index < 0 ? (delta > 0 ? 0 : Rows.Count - 1) : index + delta;
        for (int cursor = start; cursor >= 0 && cursor < Rows.Count; cursor += delta)
        {
            var row = Rows[cursor];
            var message = row.Messages.FirstOrDefault(message => (!unreadOnly || message.IsUnread)
                && (!starredOnly || message.Flags.HasFlag(MailFlags.Flagged)));
            if (message != null) return unreadOnly || starredOnly ? message : row.Message;
        }
        return null;
    }

    internal void OpenListedMessage(int delta, bool unreadOnly = false, bool starredOnly = false)
    {
        if (ListedNavigationTarget(delta, unreadOnly, starredOnly) is { } message)
            OpenMessageOrDraft(message);
    }

    internal ContextMenuItem[] GeneralWorkspaceMenuItems() =>
    [
        new("☑ Select", Children: SelectionMenuItems(), ActionId: "List.SelectMenu"),
        new("🧭 Navigate list", Children: ListNavigationMenuItems(), ActionId: "Workspace.NavigateGroup"),
        new("⌨️ Keyboard shortcuts", OpenShortcutHelpModal, actionId: "List.Shortcuts")
    ];

    IReadOnlyList<ContextMenuItem> ListNavigationMenuItems() =>
    [
        new("Previous listed message", () => OpenListedMessage(-1), ListedNavigationTarget(-1) != null, actionId: "Nav.Prev"),
        new("Next listed message", () => OpenListedMessage(1), ListedNavigationTarget(1) != null, actionId: "Nav.Next"),
        new("Previous unread conversation", () => OpenListedMessage(-1, unreadOnly: true), ListedNavigationTarget(-1, unreadOnly: true) != null, actionId: "Nav.PrevUnread"),
        new("Next unread conversation", () => OpenListedMessage(1, unreadOnly: true), ListedNavigationTarget(1, unreadOnly: true) != null, actionId: "Nav.NextUnread"),
        new("Previous starred conversation", () => OpenListedMessage(-1, starredOnly: true), ListedNavigationTarget(-1, starredOnly: true) != null, actionId: "Nav.PrevStarred"),
        new("Next starred conversation", () => OpenListedMessage(1, starredOnly: true), ListedNavigationTarget(1, starredOnly: true) != null, actionId: "Nav.NextStarred"),
        new("Focus first row · Home", () => FocusListBoundary(false), Rows.Count > 0, actionId: "Nav.First"),
        new("Focus last row · End", () => FocusListBoundary(true), Rows.Count > 0, actionId: "Nav.Last"),
        new("Close reader", MailUiState.CloseMessage, MailUiState.OpenMessage != null, actionId: "Nav.CloseReader")
    ];

    internal void OpenReaderActionsMenu(MailMessageSummary message, MouseEventArgs pointer) =>
        OpenMessageMenu(MessageTargets(message), pointer.ClientX, pointer.ClientY, primary: MessageIdentityOf(message));

    internal void OpenBulkActionsMenu(MouseEventArgs pointer)
    {
        var targets = MailUiState.SelectedMessages.ToArray();
        if (targets.Length == 0) return;
        OpenMessageMenu(targets, pointer.ClientX, pointer.ClientY);
    }

    internal void AddGeneralMessageMenuItems(List<ContextMenuItem> items, MailMessageIdentity[] targets, MailMessageIdentity lead)
    {
        items.Add(new("🎯 Selection actions", Children: GeneralMessageMenuItems(targets), ActionId: "Msg.SelectionGroup"));
        items.Add(new("🧭 Navigate list", Children: ListNavigationMenuItems(), ActionId: "Workspace.NavigateGroup"));
    }

    IReadOnlyList<ContextMenuItem> GeneralMessageMenuItems(MailMessageIdentity[] targets)
    {
        var messages = targets.Select(key => MessageStore.GetSummary(key.AccountId, key.FolderFullName, key.Uid)).OfType<MailMessageSummary>().ToArray();
        var tags = messages.SelectMany(static message => message.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        List<ContextMenuItem> items =
        [
            new("⭐ Set starred", () => SetCapturedFlag(targets, MailFlags.Flagged, true), ActionId: "Msg.SetStarred"),
            new("☆ Remove stars", () => SetCapturedFlag(targets, MailFlags.Flagged, false), ActionId: "Msg.RemoveStars"),
            new("❗ Set important", () => SetCapturedFlag(targets, MailFlags.Important, true), ActionId: "Msg.SetImportant"),
            new("○ Remove importance", () => SetCapturedFlag(targets, MailFlags.Important, false), ActionId: "Msg.RemoveImportance"),
            new("📦 Archive", () => MouseActionService.RunMessagesAsync(MailMessageCommand.Archive, targets), ActionId: "Msg.Archive"),
            new("🚫 Move to Spam", () => MouseActionService.RunMessagesAsync(MailMessageCommand.Spam, targets), ActionId: "Msg.Spam"),
            new("📥 Restore to Inbox (local for local mail)", () => MouseActionService.RunMessagesAsync(MailMessageCommand.Restore, targets), ActionId: "Msg.Restore"),
            new("☀️ Wake snoozed messages", () => MouseActionService.RunMessagesAsync(MailMessageCommand.Wake, targets), messages.Any(static message => message.SnoozedUntilUtc != null), ActionId: "Msg.Wake"),
            new("⧉ Copy subjects", () => CopyCapturedSummaries(targets, static message => message.Subject), ActionId: "Msg.CopySubjects"),
            new("⧉ Copy sender addresses", () => CopyCapturedSummaries(targets, static message => message.FromAddress, distinct: true), ActionId: "Msg.CopySenders"),
            new("⧉ Copy message references", () => CopyCapturedSummaries(targets, static message => message.MessageId, distinct: true), ActionId: "Msg.CopyMessageIds"),
            new("⧉ Copy message details", () => CopyCapturedSummaries(targets, static message => $"{message.DateUtc:O}\t{message.FromAddress}\t{message.ToAddresses}\t{message.Subject}"), ActionId: "Msg.CopyDetails"),
            new("☑ Select only these messages", () => ApplyListedSelection(targets), actionId: "Msg.SelectOnlyThese")
        ];
        if (tags.Length > 0)
            items.Add(new("🏷️ Remove tag", Children: [.. tags.Select(tag => new ContextMenuItem(tag,
                () => MouseActionService.RunMessagesAsync(MailMessageCommand.RemoveTag, targets, tag), ActionId: $"Msg.RemoveTag:{tag}"))], ActionId: "Msg.SelectionRemoveTagGroup"));
        items.Add(new("☑ Select listed messages", Children: SelectionMenuItems(), ActionId: "List.SelectMenu"));
        return items;
    }

    static Task SetCapturedFlag(MailMessageIdentity[] targets, MailFlags flag, bool enabled)
    {
        var differing = targets.Where(key => MessageStore.GetSummary(key.AccountId, key.FolderFullName, key.Uid) is { } message
            && message.Flags.HasFlag(flag) != enabled).ToArray();
        return MouseActionService.RunMessagesAsync(flag == MailFlags.Flagged ? MailMessageCommand.ToggleStar : MailMessageCommand.ToggleImportant, differing);
    }

    static Task CopyCapturedSummaries(MailMessageIdentity[] targets, Func<MailMessageSummary, string> select, bool distinct = false)
    {
        IEnumerable<string> values = targets.Select(key => MessageStore.GetSummary(key.AccountId, key.FolderFullName, key.Uid))
            .OfType<MailMessageSummary>().OrderByDescending(static message => message.DateUtc).Select(select).Where(static value => !string.IsNullOrWhiteSpace(value));
        if (distinct) values = values.Distinct(StringComparer.OrdinalIgnoreCase);
        return MouseActionService.RunAsync(MouseActionService.CopyAction, string.Join(Environment.NewLine, values));
    }
}
