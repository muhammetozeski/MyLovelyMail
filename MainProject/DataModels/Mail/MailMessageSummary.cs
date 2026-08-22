namespace MyLovelyMail.MainProject.DataModels.Mail
{
    /// <summary>Standard message flags plus the app's own markers, combinable.</summary>
    [Flags]
    public enum MailFlags
    {
        None = 0,
        Seen = 1,
        Answered = 2,
        Flagged = 4,
        Deleted = 8,
        Draft = 16,
        Forwarded = 32,
        Important = 64,
        Muted = 128
    }

    /// <summary>
    /// The lightweight per-message record kept in RAM (the "mapping"). The full MIME body stays on
    /// disk in UserCache and is loaded only when the message is opened. Everything the message
    /// list, search and filters need must fit in this summary.
    /// </summary>
    public class MailMessageSummary
    {
        /// <summary>Server UID inside its folder (or a locally generated id for local folders).</summary>
        public uint Uid { get; set; }

        /// <summary>RFC Message-Id header, used for threading and duplicate detection.</summary>
        public string MessageId { get; set; } = string.Empty;

        /// <summary>Message-Id this one replies to (empty when not a reply). Normalized, no angle brackets.</summary>
        public string InReplyTo { get; set; } = string.Empty;

        /// <summary>The References header chain (oldest first), normalized ids. Empty when absent.</summary>
        public List<string> ReferenceIds { get; set; } = [];

        public string Subject { get; set; } = string.Empty;
        public string FromName { get; set; } = string.Empty;
        public string FromAddress { get; set; } = string.Empty;

        /// <summary>Display form of the To recipients ("a@x.com, b@y.com").</summary>
        public string ToAddresses { get; set; } = string.Empty;

        public DateTime DateUtc { get; set; }
        public MailFlags Flags { get; set; }
        public bool HasAttachments { get; set; }
        public long SizeBytes { get; set; }

        /// <summary>First ~160 characters of the text body, shown under the subject in the list.</summary>
        public string PreviewText { get; set; } = string.Empty;

        /// <summary>User-assigned tag names (colored labels). Empty when untagged.</summary>
        public List<string> Tags { get; set; } = [];

        /// <summary>Hidden from the lists until this moment, then it comes back unread. Null = not snoozed.</summary>
        public DateTime? SnoozedUntilUtc { get; set; }

        public bool IsUnread => !Flags.HasFlag(MailFlags.Seen);
    }
}
