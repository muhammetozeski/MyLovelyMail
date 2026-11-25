using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Stores;

/// <summary>
/// Static registry of human-readable labels, categories, and default action lists for every customizable
/// toolbar and right-click menu surface. Ensures <c>UiCustomizerModal</c> always shows clear names and
/// the full searchable action catalog even when opened from Settings while the Mail page is not mounted.
/// </summary>
public static class UiActionCatalog
{
    static readonly Dictionary<string, (string Label, string Category)> KnownActions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Message commands
        ["Msg.ToggleRead"] = ("✅ Toggle Read / Unread", "Message"),
        ["Msg.Read"] = ("✅ Mark Read", "Message"),
        ["Msg.Unread"] = ("📩 Mark Unread", "Message"),
        ["Msg.Open"] = ("📖 Open Message", "Message"),
        ["Msg.Reply"] = ("↩️ Reply", "Message"),
        ["Msg.ReplyAll"] = ("↩️ Reply All", "Message"),
        ["Msg.Forward"] = ("↪️ Forward", "Message"),
        ["Msg.ForwardAttachment"] = ("📎 Forward as .eml", "Message"),
        ["Msg.ReplyMenu"] = ("↩️ Reply ▾", "Message"),
        ["Msg.MoreActions"] = ("⚡ Message Actions ▾", "Message"),
        ["Msg.MarkersModal"] = ("⭐ Labels & Tags…", "Message"),
        ["Msg.ToggleStar"] = ("⭐ Toggle Star", "Message"),
        ["Msg.ToggleImportant"] = ("❗ Toggle Importance", "Message"),
        ["Msg.ToggleMute"] = ("🔇 Toggle Mute", "Message"),
        ["Msg.Archive"] = ("📦 Archive", "Message"),
        ["Msg.MoveModal"] = ("📂 Move…", "Message"),
        ["Msg.MoveGroup"] = ("📂 Move to ▸", "Message"),
        ["Msg.AddTagGroup"] = ("🏷️ Add tag ▸", "Message"),
        ["Msg.RemoveTagGroup"] = ("🏷️ Remove tag ▸", "Message"),
        ["Msg.SnoozeModal"] = ("⏰ Snooze…", "Message"),
        ["Msg.SnoozeGroup"] = ("💤 Snooze ▸", "Message"),
        ["Msg.Wake"] = ("☀️ Wake Snoozed", "Message"),
        ["Msg.Spam"] = ("🚫 Move to Spam", "Message"),
        ["Msg.Restore"] = ("📥 Restore to Inbox", "Message"),
        ["Msg.Trash"] = ("🗑️ Move to Trash / Delete", "Message"),
        ["Msg.SaveModal"] = ("💾 Save (.eml / .html)…", "Message"),
        ["Msg.SaveEml"] = ("📄 Save as .eml", "Message"),
        ["Msg.SaveHtml"] = ("🌐 Save as .html", "Message"),
        ["Msg.ViewSource"] = ("🧾 View Source", "Message"),
        ["Msg.CopySubject"] = ("📋 Copy Subject", "Message"),
        ["Msg.CopySender"] = ("📋 Copy Sender Address", "Message"),
        ["Msg.SenderMenu"] = ("👤 Sender ▾", "Message"),

        // Sender commands
        ["Sender.Menu"] = ("👤 Sender Actions ▾", "Sender"),
        ["Sender.OpenFolderMail"] = ("✉️ Show Sender Messages", "Sender"),
        ["Sender.OpenPerson"] = ("👤 Open Sender Profile", "Sender"),
        ["Sender.Write"] = ("✏️ Write to Sender", "Sender"),
        ["Sender.FindFrom"] = ("🔎 Find Mail from Sender", "Sender"),
        ["Sender.FindTo"] = ("🔎 Find Mail to Sender", "Sender"),
        ["Sender.MarkAllRead"] = ("✓✓ Mark All from Sender Read", "Sender"),
        ["Sender.ArchiveAll"] = ("📦 Archive All from Sender", "Sender"),
        ["Sender.TrashAll"] = ("🗑️ Trash All from Sender", "Sender"),
        ["Sender.NotifyMute"] = ("🔕 Mute Sender Notifications", "Sender"),
        ["Sender.EditRules"] = ("🪄 Edit Sender Rules", "Sender"),

        // List, Filters & Workspace
        ["List.SelectMode"] = ("☑ Selection Mode", "List & Filter"),
        ["List.SelectAll"] = ("☑️ Select All Listed", "List & Filter"),
        ["List.SelectMenu"] = ("☑ Select Menu ▾", "List & Filter"),
        ["List.ClearSelection"] = ("✕ Clear Selection", "List & Filter"),
        ["List.SortMenu"] = ("⇅ Sort Menu ▾", "List & Filter"),
        ["List.SenderView"] = ("👥 Toggle Senders / Mails", "List & Filter"),
        ["List.ToggleQuickFilters"] = ("🎛️ Quick Filters", "List & Filter"),
        ["List.ToggleAdvancedFilters"] = ("🎯 Advanced Filters", "List & Filter"),
        ["List.Shortcuts"] = ("⌨️ Keyboard Shortcuts", "Workspace"),
        ["List.EmptyTrashOrSpam"] = ("🧹 Empty Trash / Spam", "List & Filter"),
        ["Filter.Token:is:unread"] = ("📩 Unread", "Quick Filter"),
        ["Filter.Token:is:starred"] = ("⭐ Starred", "Quick Filter"),
        ["Filter.Token:is:important"] = ("❗ Important", "Quick Filter"),
        ["Filter.Token:has:attachment"] = ("📎 Attachments", "Quick Filter"),
        ["Filter.Token:is:snoozed"] = ("💤 Snoozed", "Quick Filter"),
        ["Filter.Token:newer_than:7d"] = ("📅 Last 7 days", "Quick Filter"),
        ["Filter.Token:is:muted"] = ("🔇 Muted", "Quick Filter"),
        ["Filter.Conversations"] = ("🧵 Conversations", "List & Filter"),
        ["Filter.AllFolders"] = ("🗂️ Search All Folders", "List & Filter"),
        ["Filter.SaveSearch"] = ("⭐ Save Search", "List & Filter"),
        ["Workspace.Compose"] = ("✏️ Compose New Message", "Workspace"),
        ["Workspace.SyncAll"] = ("🔄 Sync Mail Now", "Workspace"),
        ["Workspace.Settings"] = ("⚙️ Open Settings", "Workspace"),
        ["Sidebar.NewMenu"] = ("➕ New (Group / Folder) ▾", "Workspace"),
        ["Sidebar.NewAccountFolder"] = ("📁+ New Account Group", "Workspace"),

        // Navigation, Reader & Compose
        ["Nav.Prev"] = ("← Previous Message", "Navigation"),
        ["Nav.Next"] = ("→ Next Message", "Navigation"),
        ["Nav.NextUnread"] = ("📩→ Next Unread", "Navigation"),
        ["Reader.ScaleDown"] = ("A- Smaller Text", "Reader"),
        ["Reader.ScaleReset"] = ("100% Reset Text Scale", "Reader"),
        ["Reader.ScaleUp"] = ("A+ Larger Text", "Reader"),
        ["Compose.QuoteFull"] = ("Full Quote", "Compose"),
        ["Compose.QuoteTrimmed"] = ("Trimmed Quote", "Compose"),
        ["Compose.QuoteNone"] = ("No Quote", "Compose"),
        ["Compose.SaveDraft"] = ("💾 Save Draft", "Compose"),
        ["Compose.Discard"] = ("🗑️ Discard Draft", "Compose"),
        ["Compose.Send"] = ("📨 Send Message", "Compose"),

        // Account & Folder
        ["Account.Select"] = ("📬 Select Account", "Account"),
        ["Account.CopyEmail"] = ("📋 Copy Account Email", "Account"),
        ["Account.Sync"] = ("🔄 Sync Account Now", "Account"),
        ["Account.NewMailFolder"] = ("📁+ New Mail Folder…", "Account"),
        ["Account.EditNote"] = ("🏷️ Edit Account Note & Group", "Account"),
        ["Account.Settings"] = ("⚙️ Account Settings", "Account"),
        ["Account.Rules"] = ("🪄 Account Filter Rules", "Account"),
        ["Account.Compose"] = ("✏️ Compose from Account", "Account"),
        ["Folder.Open"] = ("📂 Open Folder", "Folder"),
        ["Folder.NewSubfolder"] = ("📁+ New Subfolder…", "Folder"),
        ["Folder.Rename"] = ("✏️ Rename Folder…", "Folder"),
        ["Folder.Delete"] = ("🗑️ Delete Folder…", "Folder"),
        ["Folder.TrashRetention"] = ("⏱️ Trash Auto-Delete…", "Folder"),
        ["Folder.Empty"] = ("🧹 Empty Trash / Spam…", "Folder"),
        ["Folder.MuteGroup"] = ("🔕 Mute Notifications ▸", "Folder"),
        ["Folder.ToggleBadge"] = ("🔢 Toggle Unread Badge", "Folder"),
        ["Folder.Sync"] = ("🔄 Check Folder for Mail", "Folder"),
        ["Folder.Backfill"] = ("⬇️ Download Older Mail", "Folder"),
        ["Folder.Resync"] = ("♻️ Redownload Folder", "Folder"),
        ["Folder.ReadAll"] = ("✓✓ Mark Folder Read", "Folder"),
        ["Folder.SearchUnread"] = ("🔎 Find Unread in Folder", "Folder"),
        ["Folder.Export"] = ("📦 Export Folder (.mbox)", "Folder"),
        ["Folder.Filters"] = ("🪄 Folder Filter Rules", "Folder"),
        ["Folder.Settings"] = ("⚙️ Folder Settings", "Folder"),
        ["Folder.AccountSettings"] = ("👤 Account Settings", "Folder"),
        ["Folder.GlobalSettings"] = ("🌐 Global Folder Defaults", "Folder"),
        ["Folder.ResetPrefs"] = ("↺ Reset Folder Defaults", "Folder"),
        ["Folder.Menu"] = ("⋯ Folder Menu", "Folder"),
        ["AccGroup.AddAccount"] = ("➕ Add Account", "Account"),
        ["AccGroup.QuickMailTm"] = ("⚡+ Quick Mail.tm", "Account"),
        ["AccGroup.NewFastMail"] = ("⚡ New Fast Mail", "Account"),
        ["AccGroup.AddSubfolder"] = ("📁+ New Subgroup", "Account"),
        ["AccGroup.Rename"] = ("✏️ Rename Group", "Account"),
        ["AccGroup.Delete"] = ("🗑️ Delete Account Group…", "Account"),
        ["AccGroup.Menu"] = ("⋯ Group Menu", "Account"),
    };

    static readonly Dictionary<string, string[]> DefaultIdsBySurface = new(StringComparer.OrdinalIgnoreCase)
    {
        [UiSurfaceIds.SidebarTop] = ["Sidebar.NewMenu", "Workspace.Compose", "Workspace.SyncAll"],
        [UiSurfaceIds.SidebarFolderHover] = ["Folder.ReadAll", "Folder.Export", "Folder.Resync", "Folder.Delete", "Folder.Menu"],
        [UiSurfaceIds.SidebarAccountFolderHover] = ["AccGroup.AddAccount", "AccGroup.QuickMailTm", "AccGroup.Delete", "AccGroup.Menu"],
        [UiSurfaceIds.ListToolbar] = ["List.SelectMode", "List.SelectMenu", "List.SortMenu", "List.SenderView", "List.ToggleQuickFilters", "List.ToggleAdvancedFilters", "List.Shortcuts"],
        [UiSurfaceIds.QuickFiltersBar] = ["Filter.Token:is:unread", "Filter.Token:is:starred", "Filter.Token:is:important", "Filter.Token:has:attachment", "Filter.Token:is:snoozed", "Filter.Token:newer_than:7d", "Filter.Token:is:muted", "Filter.Conversations"],
        [UiSurfaceIds.BulkToolbar] = ["Msg.MoreActions", "Msg.Read", "Msg.Unread", "Msg.MarkersModal", "Msg.MoveModal", "Msg.SnoozeModal", "Msg.Trash", "List.SelectAll", "List.ClearSelection"],
        [UiSurfaceIds.MailRowHover] = ["Msg.ToggleRead", "Msg.Trash", "Msg.MoreActions"],
        [UiSurfaceIds.SenderRowHover] = ["Sender.OpenFolderMail", "Sender.Write", "Sender.MarkAllRead", "Sender.Menu"],
        [UiSurfaceIds.ReaderHeaderNav] = ["Nav.Prev", "Nav.Next", "Nav.NextUnread"],
        [UiSurfaceIds.ReaderToolbar] = ["Msg.ToggleRead", "Msg.ReplyMenu", "Msg.MarkersModal", "Msg.Archive", "Msg.MoveModal", "Msg.Trash", "Msg.MoreActions", "Msg.SenderMenu", "Reader.ScaleDown", "Reader.ScaleReset", "Reader.ScaleUp"],
        [UiSurfaceIds.PersonActionsBar] = ["Sender.Menu", "Sender.FindFrom", "Sender.FindTo", "Sender.Write"],
        [UiSurfaceIds.ComposeQuoteBar] = ["Compose.QuoteFull", "Compose.QuoteTrimmed", "Compose.QuoteNone"],
        [UiSurfaceIds.ComposeFooterBar] = ["Compose.SaveDraft", "Compose.Discard", "Compose.Send"],
        [UiSurfaceIds.MessageContextMenu] = ["Msg.ToggleRead", "Msg.MarkersModal", "Msg.Reply", "Msg.ReplyAll", "Msg.Forward", "Msg.ForwardAttachment", "Msg.Archive", "Msg.MoveGroup", "Msg.SnoozeGroup", "Msg.Spam", "Msg.Trash", "Msg.SaveModal", "Msg.ViewSource", "Msg.SenderMenu"],
        [UiSurfaceIds.SenderContextMenu] = ["Sender.OpenPerson", "Sender.Write", "Sender.FindFrom", "Sender.FindTo", "Sender.MarkAllRead", "Sender.ArchiveAll", "Sender.TrashAll", "Sender.NotifyMute", "Sender.EditRules"],
        [UiSurfaceIds.FolderContextMenu] = ["Folder.Open", "Folder.NewSubfolder", "Folder.Rename", "Folder.Delete", "Folder.Sync", "Folder.Backfill", "Folder.Resync", "Folder.ReadAll", "Folder.SearchUnread", "Folder.Export", "Folder.Filters", "Folder.Settings", "Folder.AccountSettings", "Folder.GlobalSettings"],
        [UiSurfaceIds.AccountContextMenu] = ["Account.Select", "Account.Compose", "Account.Sync", "Account.NewMailFolder", "Account.EditNote", "Account.Settings", "Account.Rules", "Account.CopyEmail"],
        [UiSurfaceIds.AccountFolderContextMenu] = ["AccGroup.AddAccount", "AccGroup.NewFastMail", "AccGroup.AddSubfolder", "AccGroup.Rename", "AccGroup.Delete"],
        [UiSurfaceIds.WorkspaceContextMenu] = ["List.SelectAll", "List.SelectMode", "Workspace.Compose", "Workspace.SyncAll", "Workspace.Settings"]
    };

    static readonly HashSet<string> FixedActionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Msg.MoreActions", "Sender.Menu", "Folder.Menu", "AccGroup.Menu", "List.ClearSelection", "Compose.Send"
    };

    /// <summary>Returns a human-readable label for any canonical or dynamic action ID.</summary>
    public static string LabelOf(string actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return string.Empty;
        if (KnownActions.TryGetValue(actionId, out var found)) return found.Label;
        if (actionId.StartsWith("Msg.AddTag:", StringComparison.OrdinalIgnoreCase))
            return $"🏷️ Tag: {actionId["Msg.AddTag:".Length..]}";
        if (actionId.StartsWith("Msg.RemoveTag:", StringComparison.OrdinalIgnoreCase))
            return $"✕ Remove tag: {actionId["Msg.RemoveTag:".Length..]}";
        if (actionId.StartsWith("Msg.Move:", StringComparison.OrdinalIgnoreCase))
            return $"📂 Move to {actionId["Msg.Move:".Length..].Split(['/', '.']).LastOrDefault()}";
        if (actionId.StartsWith("Msg.Snooze:", StringComparison.OrdinalIgnoreCase))
            return $"💤 Snooze: {actionId["Msg.Snooze:".Length..]}";
        if (actionId.StartsWith("Sender.TagAll:", StringComparison.OrdinalIgnoreCase))
            return $"🏷️ Tag sender: {actionId["Sender.TagAll:".Length..]}";
        if (actionId.StartsWith("Sender.RuleTag:", StringComparison.OrdinalIgnoreCase))
            return $"🪄 Rule tag: {actionId["Sender.RuleTag:".Length..]}";
        if (actionId.StartsWith("Sender.MoveAll:", StringComparison.OrdinalIgnoreCase))
            return $"📂 Move sender → {actionId["Sender.MoveAll:".Length..].Split(['/', '.']).LastOrDefault()}";
        if (actionId.StartsWith("Filter.Token:", StringComparison.OrdinalIgnoreCase))
            return $"🔎 Filter: {actionId["Filter.Token:".Length..]}";

        // Friendly fallback for any future or menu slug IDs
        string clean = actionId.Contains('.') ? actionId[(actionId.IndexOf('.') + 1)..] : actionId;
        return clean.Replace('_', ' ');
    }

    public static string CategoryOf(string actionId)
    {
        if (KnownActions.TryGetValue(actionId, out var found)) return found.Category;
        if (actionId.StartsWith("Msg.AddTag:", StringComparison.OrdinalIgnoreCase)) return "1-Click Tag";
        if (actionId.StartsWith("Msg.RemoveTag:", StringComparison.OrdinalIgnoreCase)) return "1-Click Untag";
        if (actionId.StartsWith("Msg.Move:", StringComparison.OrdinalIgnoreCase)) return "1-Click Move";
        if (actionId.StartsWith("Msg.Snooze:", StringComparison.OrdinalIgnoreCase)) return "1-Click Snooze";
        if (actionId.StartsWith("Sender.", StringComparison.OrdinalIgnoreCase)) return "Sender";
        return "Command";
    }

    /// <summary>Builds the full promotable catalog without requiring the Mail page instance to be mounted.</summary>
    public static List<CatalogEntryDescriptor> BuildStaticCatalog()
    {
        List<CatalogEntryDescriptor> list = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void Add(string id, string label, string category)
        {
            if (seen.Add(id))
                list.Add(new CatalogEntryDescriptor(id, label, category, FixedActionIds.Contains(id)));
        }

        foreach (string tag in TagStore.AllTags)
        {
            Add($"Msg.AddTag:{tag}", $"🏷️ {tag}", "1-Click Tag");
            Add($"Msg.RemoveTag:{tag}", $"✕ Remove tag: {tag}", "1-Click Untag");
            Add($"Sender.TagAll:{tag}", $"🏷️ Tag all from sender: {tag}", "Sender Tag");
            Add($"Sender.RuleTag:{tag}", $"🪄 Rule: Tag future '{tag}'", "Sender Rule");
        }

        var acc = MailUiState.SelectedAccount ?? AccountStore.Accounts.FirstOrDefault();
        if (acc != null)
        {
            foreach (var folder in MessageStore.GetFolders(acc.Id).Where(static f => f.Selectable && f.Role is not (FolderRole.Drafts or FolderRole.Outbox)))
            {
                Add($"Msg.Move:{folder.FullName}", $"📂 Move to {folder.DisplayName}", "1-Click Move");
                Add($"Sender.MoveAll:{folder.FullName}", $"📂 Move sender mail to {folder.DisplayName}", "Sender Move");
            }
        }

        foreach (var preset in SnoozeService.Presets)
            Add($"Msg.Snooze:{preset.Label}", $"💤 Snooze: {preset.Label}", "1-Click Snooze");

        foreach (var (id, info) in KnownActions)
            Add(id, info.Label, info.Category);

        return list;
    }

    /// <summary>Fallback surface inspection when the customizer is opened from Settings (outside Mail).</summary>
    public static SurfaceInspectionSnapshot InspectFallback(string surfaceId)
    {
        string[] defaultIds = DefaultIdsBySurface.TryGetValue(surfaceId, out var ids) ? ids : [];
        var defaultDescriptors = defaultIds
            .Select(id => new CatalogEntryDescriptor(id, LabelOf(id), CategoryOf(id), FixedActionIds.Contains(id)))
            .ToList();

        var visible = UiCustomizationStore.ApplyLayout(
            surfaceId,
            defaultDescriptors,
            static d => d.ActionId,
            static d => d.IsFixed,
            id => new CatalogEntryDescriptor(id, LabelOf(id), CategoryOf(id), FixedActionIds.Contains(id)));

        var visibleSet = new HashSet<string>(visible.Select(static v => v.ActionId), StringComparer.OrdinalIgnoreCase);
        var hiddenDefaults = defaultDescriptors
            .Where(d => !visibleSet.Contains(d.ActionId) && UiCustomizationStore.IsHidden(surfaceId, d.ActionId))
            .ToList();

        return new SurfaceInspectionSnapshot(surfaceId, visible, hiddenDefaults, BuildStaticCatalog());
    }
}
