namespace MyLovelyMail.MainProject.DataModels.Mail
{
    /// <summary>An in-progress outgoing message edited in the compose pane (plain text body).</summary>
    public class ComposeDraft
    {
        public MailAccountData? Account { get; set; }
        public string To { get; set; } = string.Empty;
        public string Cc { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
    }
}
