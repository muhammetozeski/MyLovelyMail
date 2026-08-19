using System.Globalization;
using MyLovelyMail.MainProject.Constants;
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
    /// <c>from:</c>, <c>to:</c>, <c>tag:</c>, <c>has:attachment</c>, <c>is:unread|starred|important|snoozed</c>,
    /// <c>after:</c>/<c>before:</c>/<c>on:</c> (yyyy-MM-dd) and <c>newer_than:</c>/<c>older_than:</c> (7d, 2w, 3m, 1y).
    /// </summary>
    public static class SearchService
    {
        /// <summary>Upper bound on returned hits so a broad query cannot flood the list.</summary>
        const int MaxHits = 500;

        /// <summary>
        /// The query compiled into one predicate. Both search paths (all-folders and the open
        /// folder) run this, so the token language the help modal advertises works everywhere
        /// instead of only in all-folders mode.
        /// </summary>
        public static Func<MailMessageSummary, bool> BuildMatcher(string query)
        {
            var (textTokens, predicates) = ParseQuery(query);
            // Snoozed mail is hidden EVERYWHERE unless the query asks for it. Living here means the
            // folder list, the all-folders search and the debug list can never disagree about it.
            bool showSnoozed = query.Contains(SnoozedToken, StringComparison.OrdinalIgnoreCase);
            return summary => (showSnoozed || summary.SnoozedUntilUtc == null)
                && predicates.All(matches => matches(summary))
                && textTokens.All(token => MatchesAnywhere(summary, token));
        }

        const string SnoozedToken = "is:snoozed";

        /// <summary>True when the query carries plain words on top of its tokens (a pure-token query keeps conversation grouping on).</summary>
        public static bool HasFreeText(string query) => ParseQuery(query).TextTokens.Count > 0;

        public static List<SearchHit> Search(string accountId, string query)
        {
            var matcher = BuildMatcher(query);
            List<SearchHit> hits = [];

            foreach (var folder in MessageStore.GetFolders(accountId))
            {
                foreach (var summary in MessageStore.GetSummaries(accountId, folder.FullName))
                {
                    if (!matcher(summary)) continue;

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
                    case "is" when value.Equals("important", StringComparison.OrdinalIgnoreCase):
                        predicates.Add(static s => s.Flags.HasFlag(MailFlags.Important));
                        break;
                    case "is" when value.Equals("snoozed", StringComparison.OrdinalIgnoreCase):
                        predicates.Add(static s => s.SnoozedUntilUtc != null);
                        break;
                    case "after" when TryReadLocalDay(value, out var afterUtc):
                        predicates.Add(s => s.DateUtc >= afterUtc);
                        break;
                    case "before" when TryReadLocalDay(value, out var beforeUtc):
                        predicates.Add(s => s.DateUtc < beforeUtc);
                        break;
                    // Half-open day, not a date equality test: DateUtc carries a time of day.
                    case "on" when TryReadLocalDay(value, out var dayStartUtc):
                        var dayEndUtc = dayStartUtc.AddDays(1);
                        predicates.Add(s => s.DateUtc >= dayStartUtc && s.DateUtc < dayEndUtc);
                        break;
                    case "newer_than" when TryReadSpan(value, out var newerSpan):
                        var newerThanUtc = DateTime.UtcNow - newerSpan;
                        predicates.Add(s => s.DateUtc >= newerThanUtc);
                        break;
                    case "older_than" when TryReadSpan(value, out var olderSpan):
                        var olderThanUtc = DateTime.UtcNow - olderSpan;
                        predicates.Add(s => s.DateUtc < olderThanUtc);
                        break;
                    default:
                        textTokens.Add(token);
                        break;
                }
            }
            return (textTokens, predicates);
        }

        /// <summary>
        /// Reads a typed <c>yyyy-MM-dd</c> as the START of that day where the user lives, in UTC.
        /// The list shows local times while <see cref="MailMessageSummary.DateUtc"/> is UTC, so
        /// comparing against a bare parsed date would put the boundary hours off.
        /// A value that will not parse returns false, and the token falls through to free text
        /// exactly as before — the date operators are purely additive.
        /// </summary>
        static bool TryReadLocalDay(string value, out DateTime startUtc)
        {
            if (DateTime.TryParse(value, UiCulture.Display, DateTimeStyles.None, out var parsed))
            {
                startUtc = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Local).ToUniversalTime();
                return true;
            }
            startUtc = default;
            return false;
        }

        /// <summary>Reads a relative age: <c>7d</c>, <c>2w</c>, <c>3m</c>, <c>1y</c>.</summary>
        static bool TryReadSpan(string value, out TimeSpan span)
        {
            span = default;
            if (value.Length < 2 || !int.TryParse(value[..^1], out int amount) || amount <= 0) return false;

            const int DaysPerWeek = 7, DaysPerMonth = 30, DaysPerYear = 365;
            int days = char.ToLowerInvariant(value[^1]) switch
            {
                'd' => amount,
                'w' => amount * DaysPerWeek,
                'm' => amount * DaysPerMonth,
                'y' => amount * DaysPerYear,
                _ => 0
            };
            if (days == 0) return false;
            span = TimeSpan.FromDays(days);
            return true;
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
