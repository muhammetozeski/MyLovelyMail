using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services;

public enum WorkspaceDragKind
{
    None,
    Account,
    AccountFolder,
    MailFolder,
    MailMessages
}

public enum WorkspaceDropPosition
{
    None,
    Before,
    After,
    Into
}

/// <summary>
/// Pure C# pointer drag-and-drop coordinator for sidebar mail accounts, account folders,
/// IMAP/local mail folders, and message list rows. Replaces unreliable HTML5 draggable attributes.
/// </summary>
public static class WorkspaceDragService
{
    const double ActivationThresholdPx = 5.0;
    const long ClickSuppressionWindowMs = 350;

    static bool hasCandidate;
    static double startX;
    static double startY;
    static long lastDragEndedTick;

    public static bool IsDragging { get; private set; }
    public static WorkspaceDragKind Kind { get; private set; }
    public static string SourceLabel { get; private set; } = string.Empty;

    public static string DraggedAccountId { get; private set; } = string.Empty;
    public static string DraggedAccountFolderId { get; private set; } = string.Empty;
    public static string DraggedMailFolderAccountId { get; private set; } = string.Empty;
    public static string DraggedMailFolderFullName { get; private set; } = string.Empty;
    public static MailMessageIdentity[] DraggedMessages { get; private set; } = [];

    public static double PointerX { get; private set; }
    public static double PointerY { get; private set; }

    public static WorkspaceDragKind HoverTargetKind { get; private set; }
    public static string HoverTargetId { get; private set; } = string.Empty;
    public static string HoverTargetSecondaryId { get; private set; } = string.Empty;
    public static string HoverTargetLabel { get; private set; } = string.Empty;
    public static WorkspaceDropPosition DropPosition { get; private set; }

    public static event Action? OnStateChanged;
    public static event Action? OnGhostMoved;
    public static Action? OnMailFoldersReordered { get; set; }

    public static void BeginAccountCandidate(MailAccountData account, double clientX, double clientY)
    {
        if (account == null) return;
        BeginCommon(WorkspaceDragKind.Account, $"📧 {account.EmailAddress}", clientX, clientY);
        DraggedAccountId = account.Id;
    }

    public static void BeginAccountFolderCandidate(AccountFolderData folder, double clientX, double clientY)
    {
        if (folder == null) return;
        BeginCommon(WorkspaceDragKind.AccountFolder, $"📁 {folder.Name}", clientX, clientY);
        DraggedAccountFolderId = folder.Id;
    }

    public static void BeginMailFolderCandidate(MailFolderData folder, double clientX, double clientY)
    {
        if (folder == null) return;
        BeginCommon(WorkspaceDragKind.MailFolder, $"📂 {folder.DisplayName}", clientX, clientY);
        DraggedMailFolderAccountId = folder.AccountId;
        DraggedMailFolderFullName = folder.FullName;
    }

    public static void BeginMessagesCandidate(MailMessageIdentity[] rowTargets, string subjectLabel, double clientX, double clientY)
    {
        if (rowTargets == null || rowTargets.Length == 0) return;
        var effective = rowTargets.Any(MailUiState.SelectedMessages.Contains)
            ? [.. MailUiState.SelectedMessages]
            : rowTargets;
        string label = effective.Length == 1
            ? $"✉️ {(string.IsNullOrWhiteSpace(subjectLabel) ? "(no subject)" : subjectLabel)}"
            : $"✉️ {effective.Length} messages";
        BeginCommon(WorkspaceDragKind.MailMessages, label, clientX, clientY);
        DraggedMessages = effective;
    }

    static void BeginCommon(WorkspaceDragKind kind, string label, double clientX, double clientY)
    {
        hasCandidate = true;
        IsDragging = false;
        Kind = kind;
        SourceLabel = label;
        DraggedAccountId = string.Empty;
        DraggedAccountFolderId = string.Empty;
        DraggedMailFolderAccountId = string.Empty;
        DraggedMailFolderFullName = string.Empty;
        DraggedMessages = [];

        startX = clientX;
        startY = clientY;
        PointerX = clientX;
        PointerY = clientY;
        ClearHoverInternal();
    }

    public static void UpdatePointer(double clientX, double clientY)
    {
        if (!hasCandidate) return;

        PointerX = clientX;
        PointerY = clientY;

        if (!IsDragging)
        {
            double dx = clientX - startX;
            double dy = clientY - startY;
            if (Math.Sqrt(dx * dx + dy * dy) < ActivationThresholdPx)
                return;

            IsDragging = true;
            OnStateChanged?.Invoke();
            OnGhostMoved?.Invoke();
            return;
        }

        OnGhostMoved?.Invoke();
    }

    public static void HoverAccount(MailAccountData target, double offsetY, double approxHeight = 36.0)
    {
        if (!EnsureActiveDrag()) return;
        if (Kind == WorkspaceDragKind.Account)
        {
            if (string.Equals(target.Id, DraggedAccountId, StringComparison.OrdinalIgnoreCase))
            {
                ClearHoverAndNotify();
                return;
            }

            WorkspaceDropPosition pos = offsetY > approxHeight * 0.5 ? WorkspaceDropPosition.After : WorkspaceDropPosition.Before;
            var dragged = AccountStore.GetById(DraggedAccountId);
            if (dragged != null && string.Equals(
                AccountFolderStore.NormalizeFolderId(dragged.FolderId),
                AccountFolderStore.NormalizeFolderId(target.FolderId),
                StringComparison.OrdinalIgnoreCase))
            {
                var siblings = AccountStore.GetAccountsInFolder(target.FolderId);
                int srcIdx = -1, targetIdx = -1;
                for (int i = 0; i < siblings.Count; i++)
                {
                    if (string.Equals(siblings[i].Id, DraggedAccountId, StringComparison.OrdinalIgnoreCase)) srcIdx = i;
                    if (string.Equals(siblings[i].Id, target.Id, StringComparison.OrdinalIgnoreCase)) targetIdx = i;
                }
                if (srcIdx >= 0 && targetIdx >= 0)
                {
                    if (srcIdx == targetIdx - 1) pos = WorkspaceDropPosition.After;
                    else if (srcIdx == targetIdx + 1) pos = WorkspaceDropPosition.Before;
                }
            }

            SetHover(WorkspaceDragKind.Account, target.Id, target.FolderId, target.EmailAddress, pos);
        }
        else if (Kind == WorkspaceDragKind.AccountFolder)
        {
            if (AccountFolderStore.CreatesCycle(DraggedAccountFolderId, target.FolderId))
            {
                ClearHoverAndNotify();
                return;
            }
            var parentFolder = AccountFolderStore.GetById(target.FolderId);
            SetHover(WorkspaceDragKind.AccountFolder, target.FolderId, string.Empty, parentFolder?.Name ?? "Folder", WorkspaceDropPosition.Into);
        }
    }

    public static void HoverAccountFolder(AccountFolderData target, double offsetY, double approxHeight = 28.0)
    {
        if (!EnsureActiveDrag()) return;

        if (Kind == WorkspaceDragKind.Account)
        {
            SetHover(WorkspaceDragKind.AccountFolder, target.Id, string.Empty, target.Name, WorkspaceDropPosition.Into);
            return;
        }

        if (Kind == WorkspaceDragKind.AccountFolder)
        {
            if (string.Equals(target.Id, DraggedAccountFolderId, StringComparison.OrdinalIgnoreCase))
            {
                ClearHoverAndNotify();
                return;
            }

            WorkspaceDropPosition pos;
            if (offsetY < approxHeight * 0.34)
                pos = WorkspaceDropPosition.Before;
            else if (offsetY > approxHeight * 0.66)
                pos = WorkspaceDropPosition.After;
            else
                pos = WorkspaceDropPosition.Into;

            if (pos != WorkspaceDropPosition.Into)
            {
                var dragged = AccountFolderStore.Folders.FirstOrDefault(f => string.Equals(f.Id, DraggedAccountFolderId, StringComparison.OrdinalIgnoreCase));
                if (dragged != null && string.Equals(dragged.ParentFolderId ?? string.Empty, target.ParentFolderId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    var siblings = string.IsNullOrEmpty(target.ParentFolderId)
                        ? AccountFolderStore.GetRootFolders()
                        : AccountFolderStore.GetChildFolders(target.ParentFolderId);
                    int srcIdx = -1, targetIdx = -1;
                    for (int i = 0; i < siblings.Count; i++)
                    {
                        if (string.Equals(siblings[i].Id, DraggedAccountFolderId, StringComparison.OrdinalIgnoreCase)) srcIdx = i;
                        if (string.Equals(siblings[i].Id, target.Id, StringComparison.OrdinalIgnoreCase)) targetIdx = i;
                    }
                    if (srcIdx >= 0 && targetIdx >= 0)
                    {
                        if (srcIdx == targetIdx - 1 && pos == WorkspaceDropPosition.Before) pos = WorkspaceDropPosition.After;
                        else if (srcIdx == targetIdx + 1 && pos == WorkspaceDropPosition.After) pos = WorkspaceDropPosition.Before;
                    }
                }
            }

            string? candidateParent = pos == WorkspaceDropPosition.Into ? target.Id : target.ParentFolderId;
            if (AccountFolderStore.CreatesCycle(DraggedAccountFolderId, candidateParent))
            {
                ClearHoverAndNotify();
                return;
            }

            SetHover(WorkspaceDragKind.AccountFolder, target.Id, target.ParentFolderId ?? string.Empty, target.Name, pos);
        }
    }

    public static void HoverMailFolder(MailFolderData target, double offsetY, double approxHeight = 34.0)
    {
        if (!EnsureActiveDrag()) return;

        if (Kind == WorkspaceDragKind.MailMessages)
        {
            if (!target.Selectable)
            {
                ClearHoverAndNotify();
                return;
            }
            SetHover(WorkspaceDragKind.MailFolder, target.FullName, target.AccountId, target.DisplayName, WorkspaceDropPosition.Into);
            return;
        }

        if (Kind == WorkspaceDragKind.MailFolder)
        {
            if (!string.Equals(target.AccountId, DraggedMailFolderAccountId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(target.FullName, DraggedMailFolderFullName, StringComparison.OrdinalIgnoreCase))
            {
                ClearHoverAndNotify();
                return;
            }

            var allFolders = MessageStore.GetFolders(target.AccountId);
            var siblings = FolderTreeService.GetOrderedSiblings(allFolders, DraggedMailFolderFullName);
            int srcIdx = siblings.FindIndex(f => string.Equals(f.FullName, DraggedMailFolderFullName, StringComparison.Ordinal));
            int targetIdx = siblings.FindIndex(f => string.Equals(f.FullName, target.FullName, StringComparison.Ordinal));
            if (srcIdx < 0 || targetIdx < 0)
            {
                ClearHoverAndNotify();
                return;
            }

            WorkspaceDropPosition pos;
            if (srcIdx == targetIdx - 1)
                pos = WorkspaceDropPosition.After;
            else if (srcIdx == targetIdx + 1)
                pos = WorkspaceDropPosition.Before;
            else
                pos = offsetY > approxHeight * 0.5 ? WorkspaceDropPosition.After : WorkspaceDropPosition.Before;

            SetHover(WorkspaceDragKind.MailFolder, target.FullName, target.AccountId, target.DisplayName, pos);
        }
    }

    static bool EnsureActiveDrag()
    {
        if (!hasCandidate) return false;
        if (!IsDragging)
        {
            UpdatePointer(PointerX, PointerY);
        }
        return IsDragging;
    }

    static void SetHover(WorkspaceDragKind targetKind, string id, string secondaryId, string label, WorkspaceDropPosition pos)
    {
        if (HoverTargetKind == targetKind
            && HoverTargetId == id
            && HoverTargetSecondaryId == secondaryId
            && DropPosition == pos)
        {
            return;
        }

        HoverTargetKind = targetKind;
        HoverTargetId = id;
        HoverTargetSecondaryId = secondaryId;
        HoverTargetLabel = label;
        DropPosition = pos;
        OnStateChanged?.Invoke();
    }

    static void ClearHoverInternal()
    {
        HoverTargetKind = WorkspaceDragKind.None;
        HoverTargetId = string.Empty;
        HoverTargetSecondaryId = string.Empty;
        HoverTargetLabel = string.Empty;
        DropPosition = WorkspaceDropPosition.None;
    }

    static void ClearHoverAndNotify()
    {
        if (DropPosition == WorkspaceDropPosition.None && string.IsNullOrEmpty(HoverTargetId))
            return;
        ClearHoverInternal();
        OnStateChanged?.Invoke();
    }

    public static WorkspaceDropPosition GetAccountDropIndicator(string accountId) =>
        IsDragging && HoverTargetKind == WorkspaceDragKind.Account && string.Equals(HoverTargetId, accountId, StringComparison.OrdinalIgnoreCase)
            ? DropPosition
            : WorkspaceDropPosition.None;

    public static WorkspaceDropPosition GetAccountFolderDropIndicator(string folderId) =>
        IsDragging && HoverTargetKind == WorkspaceDragKind.AccountFolder && string.Equals(HoverTargetId, folderId, StringComparison.OrdinalIgnoreCase)
            ? DropPosition
            : WorkspaceDropPosition.None;

    public static WorkspaceDropPosition GetMailFolderDropIndicator(string accountId, string fullName) =>
        IsDragging
        && HoverTargetKind == WorkspaceDragKind.MailFolder
        && string.Equals(HoverTargetSecondaryId, accountId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(HoverTargetId, fullName, StringComparison.Ordinal)
            ? DropPosition
            : WorkspaceDropPosition.None;

    public static string TargetHint
    {
        get
        {
            if (!IsDragging) return string.Empty;
            if (DropPosition == WorkspaceDropPosition.None || string.IsNullOrEmpty(HoverTargetId))
                return "Esc to cancel";

            return DropPosition switch
            {
                WorkspaceDropPosition.Before => $"⬆️ Place before {HoverTargetLabel}",
                WorkspaceDropPosition.After => $"⬇️ Place after {HoverTargetLabel}",
                WorkspaceDropPosition.Into when Kind == WorkspaceDragKind.MailMessages => $"📥 Move to {HoverTargetLabel}",
                WorkspaceDropPosition.Into => $"📂 Move into {HoverTargetLabel}",
                _ => "Esc to cancel"
            };
        }
    }

    public static async Task CompleteDropAsync()
    {
        if (!hasCandidate) return;

        if (!IsDragging)
        {
            ResetInternal();
            return;
        }

        var kind = Kind;
        string draggedAcct = DraggedAccountId;
        string draggedAccFolder = DraggedAccountFolderId;
        string draggedMailAcct = DraggedMailFolderAccountId;
        string draggedMailFolder = DraggedMailFolderFullName;
        var draggedMsgs = DraggedMessages;

        var targetKind = HoverTargetKind;
        string targetId = HoverTargetId;
        string targetSecondary = HoverTargetSecondaryId;
        string targetLabel = HoverTargetLabel;
        var pos = DropPosition;

        lastDragEndedTick = Environment.TickCount64;
        ResetInternal();
        OnStateChanged?.Invoke();

        if (pos == WorkspaceDropPosition.None || string.IsNullOrEmpty(targetId))
            return;

        if (kind == WorkspaceDragKind.Account)
        {
            if (targetKind == WorkspaceDragKind.Account)
            {
                bool after = pos == WorkspaceDropPosition.After;
                AccountStore.MoveToFolder(draggedAcct, targetSecondary, insertBeforeAccountId: targetId, insertAfter: after);
                MouseActionService.ReportStatus($"↕️ Placed account {(after ? "after" : "before")} '{targetLabel}'.");
            }
            else if (targetKind == WorkspaceDragKind.AccountFolder && pos == WorkspaceDropPosition.Into)
            {
                AccountFolderStore.SetExpanded(targetId, true);
                AccountStore.MoveToFolder(draggedAcct, targetId);
                MouseActionService.ReportStatus($"📂 Moved account into '{targetLabel}'.");
            }
            return;
        }

        if (kind == WorkspaceDragKind.AccountFolder && targetKind == WorkspaceDragKind.AccountFolder)
        {
            if (pos == WorkspaceDropPosition.Into)
            {
                AccountFolderStore.SetExpanded(targetId, true);
                AccountFolderStore.MoveFolder(draggedAccFolder, targetId);
                MouseActionService.ReportStatus($"📂 Moved folder into '{targetLabel}'.");
            }
            else
            {
                bool after = pos == WorkspaceDropPosition.After;
                string? parentId = string.IsNullOrEmpty(targetSecondary) ? null : targetSecondary;
                AccountFolderStore.MoveFolder(draggedAccFolder, parentId, insertBeforeFolderId: targetId, insertAfter: after);
                MouseActionService.ReportStatus($"↕️ Placed folder {(after ? "after" : "before")} '{targetLabel}'.");
            }
            return;
        }

        if (kind == WorkspaceDragKind.MailFolder && targetKind == WorkspaceDragKind.MailFolder)
        {
            bool after = pos == WorkspaceDropPosition.After;
            MessageStore.ReorderFolder(draggedMailAcct, draggedMailFolder, targetId, insertAfter: after);
            OnMailFoldersReordered?.Invoke();
            MouseActionService.ReportStatus($"↕️ Placed folder {(after ? "after" : "before")} '{targetLabel}'.");
            return;
        }

        if (kind == WorkspaceDragKind.MailMessages && targetKind == WorkspaceDragKind.MailFolder && pos == WorkspaceDropPosition.Into)
        {
            await MouseActionService.RunMessagesAsync(MailMessageCommand.Move, draggedMsgs, targetId, includeSelection: false);
        }
    }

    public static bool Cancel()
    {
        if (!hasCandidate) return false;
        bool wasDragging = IsDragging;
        ResetInternal();
        if (wasDragging)
        {
            lastDragEndedTick = Environment.TickCount64;
            OnStateChanged?.Invoke();
            MouseActionService.ReportStatus("Drag cancelled.");
        }
        return wasDragging;
    }

    public static bool ConsumeSuppressedClick()
    {
        if (lastDragEndedTick == 0) return false;
        long elapsed = Environment.TickCount64 - lastDragEndedTick;
        lastDragEndedTick = 0;
        return elapsed <= ClickSuppressionWindowMs;
    }

    static void ResetInternal()
    {
        hasCandidate = false;
        IsDragging = false;
        Kind = WorkspaceDragKind.None;
        SourceLabel = string.Empty;
        DraggedAccountId = string.Empty;
        DraggedAccountFolderId = string.Empty;
        DraggedMailFolderAccountId = string.Empty;
        DraggedMailFolderFullName = string.Empty;
        DraggedMessages = [];
        ClearHoverInternal();
    }
}
