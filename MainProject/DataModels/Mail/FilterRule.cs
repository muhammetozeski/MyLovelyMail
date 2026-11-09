namespace MyLovelyMail.MainProject.DataModels.Mail
{
    /// <summary>Which part of a message a condition inspects.</summary>
    public enum FilterField
    {
        From,
        To,
        Subject,
        BodyPreview,
        SenderDomain,
        SizeKb,
        HasAttachment,
        FromAddress,
        ReceivedDate,
        IsUnread,
        IsStarred,
        IsImportant,
        IsMuted,
        Tag
    }

    /// <summary>How the condition compares its value. Numeric operators apply to SizeKb only.</summary>
    public enum FilterOperator
    {
        Contains,
        NotContains,
        Equals,
        StartsWith,
        EndsWith,
        MatchesRegex,
        GreaterThan,
        LessThan
    }

    /// <summary>AND/OR combination of a rule's conditions.</summary>
    public enum FilterMatchMode
    {
        All,
        Any
    }

    /// <summary>What a matched rule does. Argument meaning depends on the type (see summary of each value).</summary>
    public enum FilterActionType
    {
        /// <summary>Argument: server folder full name — the message is moved on the server.</summary>
        MoveToRemoteFolder,
        /// <summary>Argument: local folder name — the message is moved into a local-only folder.</summary>
        MoveToLocalFolder,
        MarkRead,
        MarkImportant,
        /// <summary>Silences every notification of this message.</summary>
        Mute,
        /// <summary>Argument: sound name used instead of the default notification sound.</summary>
        SetNotificationSound,
        /// <summary>Argument: tag name added to the message.</summary>
        AddTag,
        Flag,
        MarkUnread,
        ClearImportant,
        Unflag,
        Unmute,
        RemoveTag
    }

    /// <summary>
    /// One field/operator/value test of a rule. <see cref="Value"/> is always text: for SizeKb it
    /// holds the numeric threshold as a string, for HasAttachment "false" means "has none".
    /// </summary>
    public class FilterCondition
    {
        public FilterField Field { get; set; } = FilterField.From;
        public FilterOperator Operator { get; set; } = FilterOperator.Contains;
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>One action a matched rule executes; what <see cref="Argument"/> means per type is documented on <see cref="FilterActionType"/>.</summary>
    public class FilterAction
    {
        public FilterActionType Type { get; set; } = FilterActionType.MarkRead;
        public string Argument { get; set; } = string.Empty;
    }

    /// <summary>
    /// One scoped incoming-mail rule. Scope priority precedes saved order; the first matching
    /// action in each conflicting family wins. <see cref="StopProcessing"/> ends remaining rules
    /// at this and lower priorities. Conditions combine using <see cref="MatchMode"/>.
    /// </summary>
    public class FilterRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;

        /// <summary>Empty applies the rule to every account.</summary>
        public string AccountId { get; set; } = string.Empty;

        /// <summary>Exact sender mailbox, matched without its display name. Empty inherits the broader scope.</summary>
        public string SenderAddress { get; set; } = string.Empty;

        /// <summary>Account-container folder ID; includes descendant containers. Deleted IDs match nothing.</summary>
        public string AccountFolderId { get; set; } = string.Empty;

        /// <summary>Exact source mailbox path. Requires AccountId and is evaluated before any move.</summary>
        public string FolderFullName { get; set; } = string.Empty;

        public FilterMatchMode MatchMode { get; set; } = FilterMatchMode.All;
        public List<FilterCondition> Conditions { get; set; } = [];
        public List<FilterAction> Actions { get; set; } = [];
        public bool StopProcessing { get; set; }
    }
}
