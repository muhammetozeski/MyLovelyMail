namespace MyLovelyMail.MainProject.DataModels.Mail
{
    /// <summary>Well-known role of a mail folder, used for icons and default behaviors.</summary>
    public enum FolderRole
    {
        None,
        Inbox,
        Sent,
        Drafts,
        Trash,
        Junk,
        Archive,
        Outbox,
        Flagged,
        AllMail
    }

    /// <summary>
    /// Which side of a folder is allowed to change the other. Read through
    /// <c>Services.Mail.FolderSyncPolicy</c>, which resolves it per role and per account.
    /// </summary>
    public enum FolderSyncDirection
    {
        /// <summary>Read the server; never write anything back to it.</summary>
        ServerToLocal,

        /// <summary>Push what happens here; never read the server's copy of this folder.</summary>
        LocalToServer,

        /// <summary>Both directions.</summary>
        TwoWay,

        /// <summary>Neither: the folder lives on this machine only.</summary>
        LocalOnly
    }

    /// <summary>
    /// One mail folder of an account — either a server folder mirrored locally or a local-only
    /// folder created by the user/filters. Server identity is <see cref="FullName"/> (the IMAP
    /// path); sync state (<see cref="UidValidity"/>, <see cref="LastSeenUid"/>) lets incremental
    /// fetches resume where they stopped.
    /// </summary>
    public class MailFolderData
    {
        public string AccountId { get; set; } = string.Empty;

        /// <summary>Full server path of the folder (e.g. "INBOX/Receipts"). For local folders: "Local/&lt;name&gt;".</summary>
        public string FullName { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;
        public FolderRole Role { get; set; } = FolderRole.None;

        /// <summary>True for folders that exist only on this machine (never synced to the server).</summary>
        public bool IsLocal { get; set; }

        /// <summary>
        /// False for a server folder marked \NoSelect: it exists and holds children, but no
        /// message can live in it. Such folders used to be dropped from the list entirely, which
        /// orphaned everything nested under them.
        /// </summary>
        public bool Selectable { get; set; } = true;

        /// <summary>The server's path separator for this folder ('/' or '.'), so a nested path can be split. 0 = not recorded yet.</summary>
        public char Delimiter { get; set; }

        public uint UidValidity { get; set; }
        public uint LastSeenUid { get; set; }

        /// <summary>
        /// Lowest uid the app has ever fetched here. Incremental sync only asks for uids ABOVE
        /// <see cref="LastSeenUid"/>, so this is the floor the backfill digs below; 0 means the
        /// folder has never been filled.
        /// </summary>
        public uint OldestFetchedUid { get; set; }

        /// <summary>When this folder's MESSAGES were last fetched — not its counts, which the folder-list pass refreshes every time.</summary>
        public DateTime? LastSyncedUtc { get; set; }

        public int TotalCount { get; set; }
        public int UnreadCount { get; set; }
    }
}
