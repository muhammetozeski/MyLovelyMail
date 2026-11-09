using Microsoft.AspNetCore.Components;
using MyLovelyMail.MainProject.UI.Components;
using MyLovelyMail.MainProject.UI.Elements.Modals;
using MyLovelyMail.MainProject.UI.Pages;

namespace MyLovelyMail.MainProject.Services;

/// <summary>Describes a modal component instance and its parameters on the navigation stack.</summary>
public sealed record ModalDescriptor(Type ComponentType, IReadOnlyDictionary<string, object?> Parameters, ModalSize Size = ModalSize.Workspace);

/// <summary>
/// Manages an in-window modal navigation stack so wizards, dialogs, compact popovers, and confirmations
/// open inside the unified CoreModal hierarchy without leaving the current page or using browser dialogs.
/// </summary>
public static class ModalService
{
    static readonly List<ModalDescriptor> stack = [];
    static TaskCompletionSource<bool>? pendingConfirmation;

    public static event Action? OnChanged;

    public static ModalDescriptor? Current => stack.Count > 0 ? stack[^1] : null;
    public static ModalDescriptor? Active => Current;
    public static bool IsOpen => stack.Count > 0;
    public static bool CanGoBack => stack.Count > 1;

    public static void Open<TComponent>(Dictionary<string, object?>? parameters = null, ModalSize size = ModalSize.Workspace)
        where TComponent : IComponent
    {
        stack.Add(new ModalDescriptor(typeof(TComponent), parameters ?? new Dictionary<string, object?>(), size));
        OnChanged?.Invoke();
    }

    public static void ReplaceTop<TComponent>(Dictionary<string, object?>? parameters = null, ModalSize size = ModalSize.Workspace)
        where TComponent : IComponent
    {
        if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
        stack.Add(new ModalDescriptor(typeof(TComponent), parameters ?? new Dictionary<string, object?>(), size));
        OnChanged?.Invoke();
    }

    public static void OpenAddAccount(string? folderId = null) =>
        Open<AccountWizard>(new Dictionary<string, object?>
        {
            [nameof(AccountWizard.IsModal)] = true,
            [nameof(AccountWizard.InitialFolderId)] = folderId
        }, ModalSize.Workspace);

    public static void PushFastMail(string? folderId = null) =>
        Open<FastMailModal>(new Dictionary<string, object?>
        {
            [nameof(FastMailModal.InitialFolderId)] = folderId
        }, ModalSize.Wide);

    public static void OpenFastMail(string? folderId = null)
    {
        stack.Clear();
        PushFastMail(folderId);
    }

    public static void OpenAccountNote(string accountId) =>
        Open<AccountNoteModal>(new Dictionary<string, object?>
        {
            [nameof(AccountNoteModal.AccountId)] = accountId
        }, ModalSize.Dialog);

    public static void OpenCreateFolder(string? parentFolderId = null) =>
        Open<AccountFolderModal>(new Dictionary<string, object?>
        {
            [nameof(AccountFolderModal.ParentFolderId)] = parentFolderId,
            [nameof(AccountFolderModal.IsRename)] = false
        }, ModalSize.Dialog);

    public static void OpenRenameFolder(string folderId) =>
        Open<AccountFolderModal>(new Dictionary<string, object?>
        {
            [nameof(AccountFolderModal.FolderId)] = folderId,
            [nameof(AccountFolderModal.IsRename)] = true
        }, ModalSize.Dialog);

    public static void Close() => GoBack();

    /// <summary>Opens a unified confirmation modal (replacing browser confirm) and awaits the user's decision.</summary>
    public static Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm",
        string cancelLabel = "Cancel", bool isDanger = false)
    {
        pendingConfirmation?.TrySetResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingConfirmation = tcs;

        Open<ConfirmDialogModal>(new Dictionary<string, object?>
        {
            [nameof(ConfirmDialogModal.Title)] = title,
            [nameof(ConfirmDialogModal.Message)] = message,
            [nameof(ConfirmDialogModal.ConfirmLabel)] = confirmLabel,
            [nameof(ConfirmDialogModal.CancelLabel)] = cancelLabel,
            [nameof(ConfirmDialogModal.IsDanger)] = isDanger,
            [nameof(ConfirmDialogModal.IsAlertOnly)] = false,
            [nameof(ConfirmDialogModal.OnDecision)] = (Action<bool>)(accepted =>
            {
                if (ReferenceEquals(pendingConfirmation, tcs))
                    pendingConfirmation = null;
                CloseTopInternal();
                tcs.TrySetResult(accepted);
            })
        }, ModalSize.Dialog);

        return tcs.Task;
    }

    /// <summary>Opens a unified alert modal (replacing browser alert) and awaits dismissal.</summary>
    public static Task AlertAsync(string title, string message, string okLabel = "OK")
    {
        pendingConfirmation?.TrySetResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingConfirmation = tcs;

        Open<ConfirmDialogModal>(new Dictionary<string, object?>
        {
            [nameof(ConfirmDialogModal.Title)] = title,
            [nameof(ConfirmDialogModal.Message)] = message,
            [nameof(ConfirmDialogModal.ConfirmLabel)] = okLabel,
            [nameof(ConfirmDialogModal.IsAlertOnly)] = true,
            [nameof(ConfirmDialogModal.OnDecision)] = (Action<bool>)(_ =>
            {
                if (ReferenceEquals(pendingConfirmation, tcs))
                    pendingConfirmation = null;
                CloseTopInternal();
                tcs.TrySetResult(true);
            })
        }, ModalSize.Dialog);

        return tcs.Task;
    }

    static void CloseTopInternal()
    {
        if (stack.Count > 0)
            stack.RemoveAt(stack.Count - 1);
        OnChanged?.Invoke();
    }

    public static void GoBack()
    {
        if (pendingConfirmation is { } pending)
        {
            pendingConfirmation = null;
            pending.TrySetResult(false);
        }
        if (stack.Count == 0) return;
        stack.RemoveAt(stack.Count - 1);
        OnChanged?.Invoke();
    }

    public static void CloseAll()
    {
        if (pendingConfirmation is { } pending)
        {
            pendingConfirmation = null;
            pending.TrySetResult(false);
        }
        if (stack.Count == 0) return;
        stack.Clear();
        OnChanged?.Invoke();
    }
}
