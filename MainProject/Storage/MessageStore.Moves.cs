using System.Text.Json;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Storage;

public static partial class MessageStore
{
    /// <summary>Prefix identifying mail that has no recoverable server copy.</summary>
    public const string LocalFolderPrefix = "Local/";
    /// <summary>The protocol inbox spelling used by IMAP and the single POP3 mailbox.</summary>
    public const string InboxFullName = "INBOX";

    /// <summary>Chooses an unused local number without overwriting a different message.</summary>
    /// <param name="accountId">Owning account.</param>
    /// <param name="targetFullName">Local destination path.</param>
    /// <param name="summary">Source metadata, left unchanged.</param>
    /// <returns>The incoming number or a collision-free local number.</returns>
    static uint FreeLocalUid(string accountId, string targetFullName, MailMessageSummary summary)
    {
        var index = GetIndex(accountId, targetFullName);
        if (!index.Summaries.TryGetValue(summary.Uid, out var occupant) || SameMessage(occupant, summary))
            return summary.Uid;
        uint candidate = StableHash.Fnv1a(summary.MessageId.Length > 0
            ? summary.MessageId
            : $"{summary.FromAddress}|{summary.Subject}|{summary.DateUtc:O}");
        while (index.Summaries.TryGetValue(candidate, out var taken) && !SameMessage(taken, summary)) candidate++;
        Log($"Local folder '{targetFullName}' already used uid {summary.Uid}; filed this message under {candidate} instead.");
        return candidate;
    }

    /// <summary>Compares stable message identity rather than folder-local message numbers.</summary>
    /// <param name="first">First cached message.</param>
    /// <param name="second">Second cached message.</param>
    /// <returns>Whether both describe the same mail.</returns>
    static bool SameMessage(MailMessageSummary first, MailMessageSummary second) =>
        first.MessageId.Length > 0 || second.MessageId.Length > 0
            ? first.MessageId.Equals(second.MessageId, StringComparison.OrdinalIgnoreCase)
            : first.FromAddress == second.FromAddress && first.Subject == second.Subject && first.DateUtc == second.DateUtc;

    /// <summary>
    /// Persists the destination metadata and MIME before removing the source. Local filing and
    /// confirmed server moves share this path. A separate summary prevents a cross-folder view
    /// from observing the same mutable object under two different identities during notifications.
    /// </summary>
    /// <param name="accountId">Owning account.</param>
    /// <param name="fromFolderFullName">Exact source folder.</param>
    /// <param name="uid">Source number.</param>
    /// <param name="targetFullName">Exact destination folder; Local/ paths are created when absent.</param>
    /// <param name="confirmedServerUid">Server-confirmed destination number; required for non-local targets.</param>
    public static void MoveCachedMessage(string accountId, string fromFolderFullName, uint uid,
        string targetFullName, uint? confirmedServerUid = null)
    {
        if (fromFolderFullName == targetFullName) return;
        bool local = IsLocalFolder(targetFullName);
        if (!local && confirmedServerUid is null or 0)
            throw new ArgumentException("A server-folder move needs its confirmed destination number.", nameof(confirmedServerUid));
        var source = GetSummary(accountId, fromFolderFullName, uid);
        if (source == null) return;
        if (local && !GetFolders(accountId).Any(folder => folder.FullName == targetFullName))
            SaveFolder(new MailFolderData { AccountId = accountId, FullName = targetFullName,
                DisplayName = targetFullName[LocalFolderPrefix.Length..], IsLocal = true });
        var moved = JsonSerializer.Deserialize<MailMessageSummary>(JsonSerializer.SerializeToUtf8Bytes(source, JsonDefaults.SingleLine), JsonDefaults.SingleLine)
            ?? throw new InvalidOperationException("Could not copy the cached message metadata.");
        moved.Uid = confirmedServerUid ?? FreeLocalUid(accountId, targetFullName, source);
        if (TryLoadFullMessage(accountId, fromFolderFullName, uid) is { } mime)
            SaveFullMessage(accountId, targetFullName, moved.Uid, mime);
        UpsertSummaries(accountId, targetFullName, [moved]);
        RemoveMessages(accountId, fromFolderFullName, [uid]);
    }
}
