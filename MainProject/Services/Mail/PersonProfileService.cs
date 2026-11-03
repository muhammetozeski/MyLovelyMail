using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>Everything the cache knows about one correspondent, gathered in a single walk.</summary>
    public sealed record PersonProfile(
        string Address,
        string DisplayName,
        int MessageCount,
        int ReceivedCount,
        int SentCount,
        int UnreadCount,
        int WithAttachments,
        DateTime OldestUtc,
        DateTime NewestUtc,
        Dictionary<string, int> ByFolder,
        List<string> Tags);

    /// <summary>
    /// Aggregates one address across every cached folder. The reader could show three numbers
    /// about a sender and nothing else in the app gathered anything per human, although every
    /// field needed — role, read state, attachments, tags, dates — was already sitting in the
    /// summary index that search and the contact list walk anyway.
    /// <para>
    /// Keyed strictly on the address. Grouping by display name would collapse every
    /// Support/Info/noreply sender into one fabricated person, and a display name is the field a
    /// sender controls most freely — a guess presented as a record is worse than no record.
    /// </para>
    /// </summary>
    public static class PersonProfileService
    {
        public static PersonProfile Build(string accountId, string address)
        {
            int received = 0, sent = 0, unread = 0, withAttachments = 0;
            DateTime oldest = DateTime.MaxValue, newest = DateTime.MinValue;
            Dictionary<string, int> byFolder = [];
            HashSet<string> tags = new(StringComparer.OrdinalIgnoreCase);
            string displayName = string.Empty;
            string ownAddress = AccountStore.GetById(accountId)?.EmailAddress ?? string.Empty;

            foreach (var folder in MessageStore.GetFolders(accountId))
            {
                foreach (var summary in MessageStore.GetSummaries(accountId, folder.FullName))
                {
                    bool fromPerson = summary.FromAddress.Equals(address, StringComparison.OrdinalIgnoreCase);
                    bool toPerson = summary.FromAddress.Equals(ownAddress, StringComparison.OrdinalIgnoreCase)
                        && ContactIndexService.Recipients(summary.ToAddresses).Any(recipient => recipient.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
                    if (!fromPerson && !toPerson) continue;

                    // A sent message can be archived or filed later; direction follows mailbox identities.
                    if (fromPerson) received++; else sent++;
                    if (summary.IsUnread) unread++;
                    if (summary.HasAttachments) withAttachments++;
                    if (summary.DateUtc < oldest) oldest = summary.DateUtc;
                    if (summary.DateUtc > newest) newest = summary.DateUtc;
                    byFolder[folder.FullName] = byFolder.GetValueOrDefault(folder.FullName) + 1;
                    foreach (string tag in summary.Tags) tags.Add(tag);

                    if (summary.FromName.Length > displayName.Length
                        && summary.FromAddress.Equals(address, StringComparison.OrdinalIgnoreCase))
                        displayName = summary.FromName;
                }
            }

            return new PersonProfile(address, displayName, received + sent, received, sent, unread, withAttachments,
                oldest == DateTime.MaxValue ? default : oldest,
                newest == DateTime.MinValue ? default : newest,
                byFolder, [.. tags.OrderBy(static t => t, StringComparer.OrdinalIgnoreCase)]);
        }
    }
}
