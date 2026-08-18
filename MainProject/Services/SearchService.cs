using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services
{
    /// <summary>One search result: the summary plus the folder it lives in (shown on the row).</summary>
    public sealed class SearchHit
    {
        public required MailMessageSummary Summary { get; init; }
        public required string FolderFullName { get; init; }
        public required string FolderDisplayName { get; init; }
    }

    /// <summary>
    /// Cross-folder search over the RAM summary index. Plain tokens must ALL match somewhere in
    /// subject/from/to/preview/tags; prefix operators narrow single fields:
    /// <c>from:</c>, <c>to:</c>, <c>tag:</c>, <c>has:attachment</c>, <c>is:unread</c>, <c>is:starred</c>.
    /// </summary>
    public static class SearchService
    {
        /// <summary>Upper bound on returned hits so a broad query cannot flood the list.</summary>
        const int MaxHits = 500;

        public static List<SearchHit> Search(string accountId, string query)
        {
            var (textTokens, predicates) = ParseQuery(query);
            List<SearchHit> hits = [];

            foreach (var folder in MessageStore.GetFolders(accountId))
            {
                foreach (var summary in MessageStore.GetSummaries(accountId, folder.FullName))
                {
                    if (!predicates.All(matches => matches(summary))) continue;
                    if (!textTokens.All(token => MatchesAnywhere(summary, token))) continue;

                    hits.Add(new SearchHit
                    {
                        Summary = summary,
                        FolderFullName = folder.FullName,
                        FolderDisplayName = folder.DisplayName
                    });
                }
            }

            return [.. hits.OrderByDescending(static h => h.Summary.DateUtc).Take(MaxHits)];
        }

        static (List<string> TextTokens, List<Func<MailMessageSummary, bool>> Predicates) ParseQuery(string query)
        {
            List<string> textTokens = [];
            List<Func<MailMessageSummary, bool>> predicates = [];

            foreach (string token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int colon = token.IndexOf(':');
                string prefix = colon > 0 ? token[..colon].ToLowerInvariant() : string.Empty;
                string value = colon > 0 ? token[(colon + 1)..] : string.Empty;

                switch (prefix)
                {
                    case "from":
                        predicates.Add(s => $"{s.FromName} {s.FromAddress}".Contains(value, StringComparison.OrdinalIgnoreCase));
                        break;
                    case "to":
                        predicates.Add(s => s.ToAddresses.Contains(value, StringComparison.OrdinalIgnoreCase));
                        break;
                    case "tag":
                        predicates.Add(s => s.Tags.Any(t => t.Contains(value, StringComparison.OrdinalIgnoreCase)));
                        break;
                    case "has" when value.Equals("attachment", StringComparison.OrdinalIgnoreCase):
                        predicates.Add(static s => s.HasAttachments);
                        break;
                    case "is" when value.Equals("unread", StringComparison.OrdinalIgnoreCase):
                        predicates.Add(static s => s.IsUnread);
                        break;
                    case "is" when value.Equals("starred", StringComparison.OrdinalIgnoreCase):
                        predicates.Add(static s => s.Flags.HasFlag(MailFlags.Flagged));
                        break;
                    default:
                        textTokens.Add(token);
                        break;
                }
            }
            return (textTokens, predicates);
        }

        static bool MatchesAnywhere(MailMessageSummary summary, string token) =>
            summary.Subject.Contains(token, StringComparison.OrdinalIgnoreCase)
            || summary.FromName.Contains(token, StringComparison.OrdinalIgnoreCase)
            || summary.FromAddress.Contains(token, StringComparison.OrdinalIgnoreCase)
            || summary.ToAddresses.Contains(token, StringComparison.OrdinalIgnoreCase)
            || summary.PreviewText.Contains(token, StringComparison.OrdinalIgnoreCase)
            || summary.Tags.Any(t => t.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}
