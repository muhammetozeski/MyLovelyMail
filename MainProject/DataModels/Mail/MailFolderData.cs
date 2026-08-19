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
