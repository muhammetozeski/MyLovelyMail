namespace MyLovelyMail.MainProject.DataModels.Mail;

/// <summary>
/// One folder in the account hierarchy, persisted in <c>UserData/account-folders.json</c>.
/// Folders are identified by GUID (<see cref="Id"/>), never by name, and may be nested via
/// <see cref="ParentFolderId"/>. The default root folder uses <see cref="Stores.AccountFolderStore.DefaultFolderId"/>
/// (<c>00000000-0000-0000-0000-000000000000</c>) and cannot be deleted.
/// </summary>
public class AccountFolderData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = "New Folder";
    public string? ParentFolderId { get; set; }
    public bool IsExpanded { get; set; } = true;
    public int SortOrder { get; set; }
}
