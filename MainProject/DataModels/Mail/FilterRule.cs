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
        HasAttachment
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
        Flag
    }

    public class FilterCondition
    {
        public FilterField Field { get; set; } = FilterField.From;
        public FilterOperator Operator { get; set; } = FilterOperator.Contains;
        public string Value { get; set; } = string.Empty;
    }

    public class FilterAction
    {
        public FilterActionType Type { get; set; } = FilterActionType.MarkRead;
        public string Argument { get; set; } = string.Empty;
    }

    /// <summary>
    /// One user-defined mail rule: conditions combined by <see cref="MatchMode"/>, applied to
    /// incoming mail of one account (or all accounts when <see cref="AccountId"/> is empty),
    /// executing every action in order. <see cref="StopProcessing"/> ends the rule chain for a
    /// message, mirroring classic mail-client rule semantics.
    /// </summary>
    public class FilterRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;

        /// <summary>Empty applies the rule to every account.</summary>
        public string AccountId { get; set; } = string.Empty;

        public FilterMatchMode MatchMode { get; set; } = FilterMatchMode.All;
        public List<FilterCondition> Conditions { get; set; } = [];
        public List<FilterAction> Actions { get; set; } = [];
        public bool StopProcessing { get; set; }
    }
}
