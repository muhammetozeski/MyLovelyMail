using Microsoft.AspNetCore.Components.Web;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.UI.Pages;

public partial class Mail
{
    // Predicates choose rows, so a collapsed conversation is always selected as a whole.
    static readonly (string Label, Func<MailMessageSummary, bool> Matches)[] SelectionChoices =
    [
        ("Unread", static message => message.IsUnread),
        ("Read", static message => !message.IsUnread),
        ("Starred", static message => message.Flags.HasFlag(MailFlags.Flagged)),
        ("Not starred", static message => !message.Flags.HasFlag(MailFlags.Flagged)),
        ("Important", static message => message.Flags.HasFlag(MailFlags.Important)),
        ("Not important", static message => !message.Flags.HasFlag(MailFlags.Important)),
        ("With attachments", static message => message.HasAttachments),
        ("Without attachments", static message => !message.HasAttachments),
        ("Tagged", static message => message.Tags.Count > 0),
        ("Untagged", static message => message.Tags.Count == 0),
        ("Muted", static message => message.Flags.HasFlag(MailFlags.Muted)),
        ("Not muted", static message => !message.Flags.HasFlag(MailFlags.Muted)),
        ("Snoozed", static message => message.SnoozedUntilUtc != null)
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
            new("All listed messages", () => ApplyListedSelection(all), all.Length > 0),
            new("Clear selection", MailUiState.ClearSelection, current.Count > 0),
            new("Invert listed selection", () => ApplyListedSelection(inverted), all.Length > 0)
        ];
        foreach (var choice in SelectionChoices)
        {
            var matching = MessageIdentities(Rows.Where(row => row.Messages.Any(choice.Matches))
                .SelectMany(static row => row.Messages));
            choices.Add(new($"{choice.Label} · {matching.Length}", () => ApplyListedSelection(matching), matching.Length > 0));
        }
        var focused = focus < 0 ? [] : MessageTargets(Rows[focus].Message);
        var before = focus < 0 ? [] : MessageIdentities(Rows.Take(focus + 1).SelectMany(static row => row.Messages));
        var after = focus < 0 ? [] : MessageIdentities(Rows.Skip(focus).SelectMany(static row => row.Messages));
        choices.Add(new("Focused conversation only", () => ApplyListedSelection(focused), focus >= 0));
        choices.Add(new("Start through focused row", () => ApplyListedSelection(before), focus >= 0));
        choices.Add(new("Focused row through end", () => ApplyListedSelection(after), focus >= 0));
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
        new("☑ Select", Children: SelectionMenuItems()),
        new("🧭 Navigate list", Children: ListNavigationMenuItems()),
        new("⌨️ Keyboard shortcuts", OpenShortcutHelpModal)
    ];

    IReadOnlyList<ContextMenuItem> ListNavigationMenuItems() =>
    [
        new("Previous listed message", () => OpenListedMessage(-1), ListedNavigationTarget(-1) != null),
        new("Next listed message", () => OpenListedMessage(1), ListedNavigationTarget(1) != null),
        new("Previous unread conversation", () => OpenListedMessage(-1, unreadOnly: true), ListedNavigationTarget(-1, unreadOnly: true) != null),
        new("Next unread conversation", () => OpenListedMessage(1, unreadOnly: true), ListedNavigationTarget(1, unreadOnly: true) != null),
        new("Previous starred conversation", () => OpenListedMessage(-1, starredOnly: true), ListedNavigationTarget(-1, starredOnly: true) != null),
        new("Next starred conversation", () => OpenListedMessage(1, starredOnly: true), ListedNavigationTarget(1, starredOnly: true) != null),
        new("Focus first row · Home", () => FocusListBoundary(false), Rows.Count > 0),
        new("Focus last row · End", () => FocusListBoundary(true), Rows.Count > 0),
        new("Close reader", MailUiState.CloseMessage, MailUiState.OpenMessage != null)
    ];

    internal void OpenListActionsMenu(MouseEventArgs pointer) => OpenWorkspaceMenu(pointer);

    internal void OpenReaderActionsMenu(MailMessageSummary message, MouseEventArgs pointer) =>
        OpenMessageMenu(MessageTargets(message), pointer.ClientX, pointer.ClientY, primary: MessageIdentityOf(message));

    internal void OpenBulkActionsMenu(MouseEventArgs pointer)
    {
        var targets = MailUiState.SelectedMessages.ToArray();
        if (targets.Length == 0) return;
        ContextMenuService.Open(this, $"{targets.Length} selected messages", GeneralMessageMenuItems(targets), pointer.ClientX, pointer.ClientY);
    }

    internal void AddGeneralMessageMenuItems(List<ContextMenuItem> items, MailMessageIdentity[] targets, MailMessageIdentity lead)
    {
        items.Add(new("🎯 Selection actions", Children: GeneralMessageMenuItems(targets)));
        items.Add(new("🧭 Navigate list", Children: ListNavigationMenuItems()));
    }

    IReadOnlyList<ContextMenuItem> GeneralMessageMenuItems(MailMessageIdentity[] targets)
    {
        var messages = targets.Select(key => MessageStore.GetSummary(key.AccountId, key.FolderFullName, key.Uid)).OfType<MailMessageSummary>().ToArray();
        var tags = messages.SelectMany(static message => message.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        List<ContextMenuItem> items =
        [
            new("⭐ Set starred", () => SetCapturedFlag(targets, MailFlags.Flagged, true)),
            new("☆ Remove stars", () => SetCapturedFlag(targets, MailFlags.Flagged, false)),
            new("❗ Set important", () => SetCapturedFlag(targets, MailFlags.Important, true)),
            new("○ Remove importance", () => SetCapturedFlag(targets, MailFlags.Important, false)),
            new("📦 Archive", () => MouseActionService.RunMessagesAsync(MailMessageCommand.Archive, targets)),
            new("🚫 Move to Spam", () => MouseActionService.RunMessagesAsync(MailMessageCommand.Spam, targets)),
            new("📥 Restore to Inbox (local for local mail)", () => MouseActionService.RunMessagesAsync(MailMessageCommand.Restore, targets)),
            new("☀️ Wake snoozed messages", () => MouseActionService.RunMessagesAsync(MailMessageCommand.Wake, targets), messages.Any(static message => message.SnoozedUntilUtc != null)),
            new("⧉ Copy subjects", () => CopyCapturedSummaries(targets, static message => message.Subject)),
            new("⧉ Copy sender addresses", () => CopyCapturedSummaries(targets, static message => message.FromAddress, distinct: true)),
            new("⧉ Copy message references", () => CopyCapturedSummaries(targets, static message => message.MessageId, distinct: true)),
            new("⧉ Copy message details", () => CopyCapturedSummaries(targets, static message => $"{message.DateUtc:O}\t{message.FromAddress}\t{message.ToAddresses}\t{message.Subject}")),
            new("☑ Select only these messages", () => ApplyListedSelection(targets))
        ];
        if (tags.Length > 0)
            items.Add(new("🏷️ Remove tag", Children: [.. tags.Select(tag => new ContextMenuItem(tag,
                () => MouseActionService.RunMessagesAsync(MailMessageCommand.RemoveTag, targets, tag)))]));
        items.Add(new("☑ Select listed messages", Children: SelectionMenuItems()));
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
