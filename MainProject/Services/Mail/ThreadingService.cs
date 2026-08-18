using System.Text.RegularExpressions;
using MyLovelyMail.MainProject.DataModels.Mail;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>One conversation: its messages oldest-first plus the list-row aggregates.</summary>
    public sealed class MailThread
    {
        public required List<MailMessageSummary> Messages { get; init; }
        public MailMessageSummary Newest => Messages[^1];
        public int UnreadCount => Messages.Count(static m => m.IsUnread);
        public List<string> ParticipantAddresses =>
            [.. Messages.Select(static m => m.FromAddress).Where(static a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Groups summaries into conversations. Primary link: Message-Id ↔ InReplyTo/References
    /// (union-find). Messages with no id links fall back to a normalized subject key (Re:/Fwd:/
    /// [tag] prefixes stripped), so replies from clients that drop References still group.
    /// Pure in-memory work over the RAM summaries — never touches disk MIME.
    /// </summary>
    public static partial class ThreadingService
    {
        [GeneratedRegex(@"^\s*((re|fwd?|fw)\s*:\s*|\[[^\]]*\]\s*)+", RegexOptions.IgnoreCase)]
        private static partial Regex ReplyPrefixes();

        /// <summary>Threads newest-first; messages inside each thread oldest-first.</summary>
        public static List<MailThread> BuildThreads(List<MailMessageSummary> summaries)
        {
            int[] parent = [.. Enumerable.Range(0, summaries.Count)];

            int Find(int node)
            {
                while (parent[node] != node)
                    node = parent[node] = parent[parent[node]];
                return node;
            }

            void Union(int a, int b) => parent[Find(a)] = Find(b);

            // Pass 1: id links. Map every known Message-Id to its index, then union each
            // message with everything its InReplyTo/References point at.
            Dictionary<string, int> indexByMessageId = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < summaries.Count; i++)
                if (summaries[i].MessageId.Length > 0)
                    indexByMessageId.TryAdd(summaries[i].MessageId, i);

            for (int i = 0; i < summaries.Count; i++)
            {
                if (summaries[i].InReplyTo.Length > 0 && indexByMessageId.TryGetValue(summaries[i].InReplyTo, out int replyTarget))
                    Union(i, replyTarget);
                foreach (string referenceId in summaries[i].ReferenceIds)
                    if (indexByMessageId.TryGetValue(referenceId, out int referenced))
                        Union(i, referenced);
            }

            // Pass 2: subject fallback for reply-looking messages whose References chain was
            // dropped by the sending client. Two independent messages that merely share a
            // subject must NOT merge, so a link is only made when at least one side carries
            // a Re:/Fwd: prefix.
            Dictionary<string, int> indexBySubject = new(StringComparer.OrdinalIgnoreCase);
            bool[] replyLike = new bool[summaries.Count];
            for (int i = 0; i < summaries.Count; i++)
            {
                string subjectKey = ReplyPrefixes().Replace(summaries[i].Subject, string.Empty).Trim();
                replyLike[i] = subjectKey.Length != summaries[i].Subject.Trim().Length;
                if (subjectKey.Length == 0) continue;

                if (indexBySubject.TryGetValue(subjectKey, out int existing))
                {
                    if (replyLike[i] || replyLike[existing])
                        Union(i, existing);
                }
                else
                {
                    indexBySubject[subjectKey] = i;
                }
            }

            return [.. summaries
                .Select((summary, index) => (summary, root: Find(index)))
                .GroupBy(static pair => pair.root)
                .Select(static group => new MailThread { Messages = [.. group.Select(static p => p.summary).OrderBy(static s => s.DateUtc)] })
                .OrderByDescending(static thread => thread.Newest.DateUtc)];
        }
    }
}
