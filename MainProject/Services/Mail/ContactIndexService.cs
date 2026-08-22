using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>One known correspondent: the address plus the nicest display name seen for it.</summary>
    public sealed record Contact(string Address, string DisplayName)
    {
        /// <summary>What the compose field offers: "Name &lt;address&gt;" when a name is known, else the bare address.</summary>
        public string Suggestion => DisplayName.Length == 0 ? Address : $"{DisplayName} <{Address}>";
    }

    /// <summary>
    /// Builds the address book out of the summaries already in RAM — everyone the account has
    /// written to or heard from — so compose can suggest instead of asking the user to retype.
    /// People you WROTE to rank above people who wrote to you, then by how often they appear.
    /// The list is cached per account and rebuilt when the store changes.
    /// </summary>
    public static class ContactIndexService
    {
        /// <summary>Addresses kept per account; a longer list stops helping and only slows the field.</summary>
        const int MaxContacts = 500;

        /// <summary>An address in a Sent folder counts this much more than one in an incoming folder.</summary>
        const int SentFolderWeight = 3;

        static readonly Dictionary<string, List<Contact>> cacheByAccount = [];
        static readonly Lock gate = new();

        static ContactIndexService() => MessageStore.OnFolderChanged += static (accountId, _) => Invalidate(accountId);

        public static void Invalidate(string accountId)
        {
            lock (gate) cacheByAccount.Remove(accountId);
        }

        /// <summary>Known correspondents, best first. <paramref name="prefix"/> filters on address or name.</summary>
        public static List<Contact> Suggest(string accountId, string prefix = "", int take = 20)
        {
            var contacts = GetOrBuild(accountId);
            if (prefix.Length > 0)
                contacts = [.. contacts.Where(c =>
                    c.Address.Contains(prefix, StringComparison.OrdinalIgnoreCase)
                    || c.DisplayName.Contains(prefix, StringComparison.OrdinalIgnoreCase))];
            return [.. contacts.Take(take)];
        }

        static List<Contact> GetOrBuild(string accountId)
        {
            lock (gate)
            {
                if (cacheByAccount.TryGetValue(accountId, out var cached)) return cached;
            }

            var built = Build(accountId);
            lock (gate) cacheByAccount[accountId] = built;
            return built;
        }

        static List<Contact> Build(string accountId)
        {
            string ownAddress = AccountStore.GetById(accountId)?.EmailAddress ?? string.Empty;
            Dictionary<string, (string Name, int Score, DateTime Newest)> byAddress = new(StringComparer.OrdinalIgnoreCase);

            void Record(string address, string name, int weight, DateTime seenUtc)
            {
                address = address.Trim().Trim('<', '>');
                if (!address.Contains('@') || address.Equals(ownAddress, StringComparison.OrdinalIgnoreCase)) return;

                byAddress.TryGetValue(address, out var existing);
                byAddress[address] = (
                    // Keep the fullest name ever seen for this address.
                    name.Length > (existing.Name?.Length ?? 0) ? name : existing.Name ?? string.Empty,
                    existing.Score + weight,
                    seenUtc > existing.Newest ? seenUtc : existing.Newest);
            }

            foreach (var folder in MessageStore.GetFolders(accountId))
            {
                int weight = folder.Role == FolderRole.Sent ? SentFolderWeight : 1;
                foreach (var summary in MessageStore.GetSummaries(accountId, folder.FullName))
                {
                    Record(summary.FromAddress, summary.FromName, weight, summary.DateUtc);
                    foreach (string recipient in summary.ToAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        Record(recipient, string.Empty, weight, summary.DateUtc);
                }
            }

            return [.. byAddress
                .OrderByDescending(entry => entry.Value.Score)
                .ThenByDescending(entry => entry.Value.Newest)
                .Take(MaxContacts)
                .Select(entry => new Contact(entry.Key, entry.Value.Name))];
        }
    }
}
