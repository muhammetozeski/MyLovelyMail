namespace MyLovelyMail.MainProject.DataModels.Mail;

/// <summary>Identifies cached mail without confusing identical numbers in different folders.</summary>
/// <param name="AccountId">Owning account.</param>
/// <param name="FolderFullName">Exact server or local folder path.</param>
/// <param name="Uid">Message number within that folder.</param>
public readonly record struct MailMessageIdentity(string AccountId, string FolderFullName, uint Uid);

/// <summary>Commands shared by mail toolbars, context menus and mouse side buttons.</summary>
public enum MailMessageCommand
{
    Read, Unread, Trash, Archive, Spam, Restore, ToggleStar, ToggleImportant,
    AddTag, RemoveTag, Snooze, Wake, Move
}

/// <summary>Optional command arguments for a declared side-button operation such as removing a particular tag.</summary>
public sealed class MailMessageRequest
{
    public MailMessageIdentity[] Messages { get; set; } = [];
    public string? Argument { get; set; }
    public DateTime? DueUtc { get; set; }
}
