namespace MyLovelyMail.MainProject.Services;

public enum AppModalKind
{
    AddAccount,
    FastMail,
    AccountNote,
    AccountFolder
}

public sealed record ModalDescriptor(
    AppModalKind Kind,
    string? FolderId = null,
    string? AccountId = null,
    string? ParentFolderId = null,
    bool IsRenameFolder = false);

/// <summary>
/// Manages the single active foreground modal and its navigation history stack.
/// Only one modal is rendered at a time; pushing a new modal onto the stack allows the Back button
/// to return to the previous modal without changing the active page route.
/// </summary>
public static class ModalService
{
    static readonly Stack<ModalDescriptor> stack = new();

    public static event Action? OnChanged;

    public static ModalDescriptor? Current => stack.TryPeek(out var top) ? top : null;

    public static bool IsOpen => stack.Count > 0;

    public static bool CanGoBack => stack.Count > 1;

    public static void Open(ModalDescriptor descriptor)
    {
        stack.Clear();
        stack.Push(descriptor);
        OnChanged?.Invoke();
    }

    public static void Push(ModalDescriptor descriptor)
    {
        stack.Push(descriptor);
        OnChanged?.Invoke();
    }

    public static void GoBack()
    {
        if (stack.Count > 0)
            stack.Pop();
        OnChanged?.Invoke();
    }

    public static void CloseAll()
    {
        if (stack.Count == 0) return;
        stack.Clear();
        OnChanged?.Invoke();
    }

    public static void OpenAddAccount(string? folderId = null) =>
        Open(new ModalDescriptor(AppModalKind.AddAccount, FolderId: folderId));

    public static void PushFastMail(string? folderId = null) =>
        Push(new ModalDescriptor(AppModalKind.FastMail, FolderId: folderId));

    public static void OpenFastMail(string? folderId = null) =>
        Open(new ModalDescriptor(AppModalKind.FastMail, FolderId: folderId));

    public static void OpenAccountNote(string accountId) =>
        Open(new ModalDescriptor(AppModalKind.AccountNote, AccountId: accountId));

    public static void OpenCreateFolder(string? parentFolderId = null) =>
        Open(new ModalDescriptor(AppModalKind.AccountFolder, ParentFolderId: parentFolderId, IsRenameFolder: false));

    public static void OpenRenameFolder(string folderId) =>
        Open(new ModalDescriptor(AppModalKind.AccountFolder, FolderId: folderId, IsRenameFolder: true));
}
