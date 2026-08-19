namespace MyLovelyMail.MainProject.DataModels.Mail
{
    /// <summary>An in-progress outgoing message edited in the compose pane (plain text body).</summary>
    public class ComposeDraft
    {
        /// <summary>Stable identity so every autosave overwrites the SAME local draft.</summary>
        public string DraftId { get; set; } = Guid.NewGuid().ToString("N");

        public MailAccountData? Account { get; set; }
        public string To { get; set; } = string.Empty;
        public string Cc { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;

        /// <summary>What this draft is quoting, so the quote can be REBUILT at another style rather than re-parsed out of the edited body.</summary>
        public string SourceFolder { get; set; } = string.Empty;
        public uint SourceUid { get; set; }

        /// <summary>Full paths of the staged attachment copies under AppCache (see ComposeService.AttachFileAsync).</summary>
        public List<string> AttachmentPaths { get; set; } = [];
    }
}
