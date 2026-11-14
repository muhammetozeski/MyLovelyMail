namespace MyLovelyMail.MainProject.DataModels.Mail;

/// <summary>Distinguishes a soft-deleted sidebar account group from a soft-deleted mailbox folder.</summary>
public enum DeletedFolderKind
{
    AccountGroup,
    MailFolder
}

/// <summary>Snapshot of one mailbox folder (and its relative backup directory) inside a <see cref="DeletedFolderEntry"/>.</summary>
public sealed class ArchivedMailFolderSnapshot
{
    public MailFolderData Folder { get; set; } = new();
    public string RelativeBackupFolder { get; set; } = string.Empty;
    public int MessageCount { get; set; }
}

/// <summary>
/// One soft-deleted folder sitting inside the dedicated Deleted Folders Trash bin (<c>UserData/DeletedFolders</c>).
/// When no <see cref="DeletedFolderEntry"/> remains, the Deleted Folders Trash bin itself is removed automatically.
/// </summary>
public sealed class DeletedFolderEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DeletedFolderKind Kind { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string FullPathLabel { get; set; } = string.Empty;
    public DateTime DeletedAtUtc { get; set; } = DateTime.UtcNow;

    // ---- AccountGroup fields ----
    public AccountFolderData? AccountFolder { get; set; }
    public string FallbackFolderId { get; set; } = string.Empty;
    public List<string> ReassignedAccountIds { get; set; } = [];
    public List<string> ReparentedChildFolderIds { get; set; } = [];

    // ---- MailFolder fields ----
    public string AccountId { get; set; } = string.Empty;
    public string AccountEmail { get; set; } = string.Empty;
    public List<ArchivedMailFolderSnapshot> MailFolders { get; set; } = [];
    public int TotalMessageCount { get; set; }
}
