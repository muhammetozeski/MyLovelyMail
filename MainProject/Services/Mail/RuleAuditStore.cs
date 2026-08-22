using System.Text;
using System.Text.Json;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>One thing a rule did to one message.</summary>
    public sealed class RuleAuditEntry
    {
        public DateTime WhenUtc { get; set; }
        public string AccountId { get; set; } = string.Empty;
        public string FolderFullName { get; set; } = string.Empty;
        public uint Uid { get; set; }

        /// <summary>Also keyed on Message-Id: a remote move deletes the source row, so the uid it was found under is gone by the time anyone asks.</summary>
        public string MessageId { get; set; } = string.Empty;

        public string RuleId { get; set; } = string.Empty;
        public string RuleName { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string Argument { get; set; } = string.Empty;
    }

    /// <summary>
    /// What the rules have already done, so "why is this message here" has an answer.
    /// <para>
    /// A rule that works leaves no trace at all today: mail simply arrives already-read, silently
    /// tagged, muted, or missing from the Inbox. The only line the engine ever logs is the one for
    /// a move that FAILED. RuleEngine.Preview answers "what would this rule do" before it is armed;
    /// nothing answered "what did it already do", which is the question asked after the surprise.
    /// </para>
    /// </summary>
    public static class RuleAuditStore
    {
        public const string FileName = "rule-audit.jsonl";

        /// <summary>Entries kept before the oldest are dropped — a diary, not an archive.</summary>
        const int MaxEntries = 2000;

        static List<RuleAuditEntry>? entries;

        static string StorePath => Path.Combine(AppPaths.UserData, FileName);

        static List<RuleAuditEntry> Entries
        {
            get
            {
                if (entries != null) return entries;
                entries = [];
                if (!File.Exists(StorePath)) return entries;
                foreach (string line in File.ReadLines(StorePath))
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        if (JsonSerializer.Deserialize<RuleAuditEntry>(line, JsonDefaults.SingleLine) is { } entry)
                            entries.Add(entry);
                    }
                    catch (Exception ex)
                    {
                        Log($"Corrupt rule-audit line skipped: {ex.Message}", LogLevel.Warning);
                    }
                }
                return entries;
            }
        }

        public static void Record(RuleAuditEntry entry)
        {
            var all = Entries;
            all.Add(entry);
            if (all.Count > MaxEntries) all.RemoveRange(0, all.Count - MaxEntries);
            Persist(all);
        }

        /// <summary>
        /// What rules did to this message, newest first. Matched on Message-Id when there is one,
        /// so an entry survives the message being moved out of the folder it was found in.
        /// </summary>
        public static List<RuleAuditEntry> For(string accountId, string folderFullName, uint uid, string messageId) =>
            [.. Entries
                .Where(e => e.AccountId == accountId
                    && (messageId.Length > 0 && e.MessageId == messageId
                        || e.FolderFullName == folderFullName && e.Uid == uid))
                .OrderByDescending(static e => e.WhenUtc)];

        public static IReadOnlyList<RuleAuditEntry> All => Entries;

        static void Persist(List<RuleAuditEntry> all)
        {
            try
            {
                var text = new StringBuilder();
                foreach (var entry in all)
                    text.AppendLine(JsonSerializer.Serialize(entry, JsonDefaults.SingleLine));
                AtomicFile.WriteAllText(StorePath, text.ToString());
            }
            catch (Exception ex)
            {
                Log($"Could not write the rule audit: {ex.Message}", LogLevel.Warning);
            }
        }
    }
}
