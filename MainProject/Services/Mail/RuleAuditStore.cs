using System.Text;
using System.Text.Json;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>One thing a rule did to one message, however many times it did it.</summary>
    public sealed class RuleAuditEntry
    {
        /// <summary>When this rule first did this to this message.</summary>
        public DateTime WhenUtc { get; set; }

        /// <summary>The most recent repeat. Equals <see cref="WhenUtc"/> while <see cref="Count"/> is 1.</summary>
        public DateTime LastSeenUtc { get; set; }

        /// <summary>How many times this exact (rule, action, message) triple has happened.</summary>
        public int Count { get; set; } = 1;

        public string AccountId { get; set; } = string.Empty;
        public string FolderFullName { get; set; } = string.Empty;
        public uint Uid { get; set; }

        /// <summary>Also keyed on Message-Id: a remote move deletes the source row, so the uid it was found under is gone by the time anyone asks.</summary>
        public string MessageId { get; set; } = string.Empty;

        public string RuleId { get; set; } = string.Empty;
        public string RuleName { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string Argument { get; set; } = string.Empty;

        /// <summary>
        /// What makes two records the same event. The message is identified by its Message-Id where
        /// the server gave one, because a move re-keys the message under a fresh uid in its new
        /// folder — the same reason <see cref="RuleAuditStore.For"/> matches on it.
        /// </summary>
        internal string Key => string.Join(
            KeySeparator,
            AccountId,
            MessageId.Length > 0 ? MessageId : $"{FolderFullName}#{Uid}",
            RuleId,
            Action,
            Argument);

        /// <summary>A character no mail header, folder path or rule id can contain.</summary>
        const char KeySeparator = (char)31; // ASCII unit separator
    }

    /// <summary>
    /// What the rules have already done, so "why is this message here" has an answer.
    /// <para>
    /// A rule that works leaves no trace at all today: mail simply arrives already-read, silently
    /// tagged, muted, or missing from the Inbox. The only line the engine ever logs is the one for
    /// a move that FAILED. RuleEngine.Preview answers "what would this rule do" before it is armed;
    /// nothing answered "what did it already do", which is the question asked after the surprise.
    /// </para>
    /// <para>
    /// One event is one record. A repeat bumps <see cref="RuleAuditEntry.Count"/> instead of adding
    /// a line, so a rule that keeps firing cannot bury the rest of the diary — a real file held 1548
    /// records of only 516 distinct events, and the reader rendered every copy as its own chip.
    /// </para>
    /// </summary>
    public static class RuleAuditStore
    {
        public const string FileName = "rule-audit.jsonl";

        /// <summary>Entries kept before the oldest are dropped — a diary, not an archive.</summary>
        const int MaxEntries = 2000;

        /// <summary>IMAP and POP3 passes for different accounts record at the same time.</summary>
        static readonly Lock gate = new();

        static List<RuleAuditEntry>? entries;

        /// <summary><see cref="RuleAuditEntry.Key"/> → the one record for that event.</summary>
        static Dictionary<string, RuleAuditEntry>? byKey;

        static string StorePath => Path.Combine(AppPaths.UserData, FileName);

        /// <summary>Loads the file once. Call inside <see cref="gate"/>.</summary>
        static List<RuleAuditEntry> Entries
        {
            get
            {
                if (entries != null) return entries;
                entries = [];
                byKey = [];
                if (!File.Exists(StorePath)) return entries;
                foreach (string line in File.ReadLines(StorePath))
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        if (JsonSerializer.Deserialize<RuleAuditEntry>(line, JsonDefaults.SingleLine) is not { } entry)
                            continue;
                        // Files written before counting existed carry neither field.
                        if (entry.Count < 1) entry.Count = 1;
                        if (entry.LastSeenUtc == default) entry.LastSeenUtc = entry.WhenUtc;

                        // Those same files hold the duplicates this class now prevents; folding them
                        // on load is the migration, and it costs one pass over a file already read.
                        if (byKey.TryGetValue(entry.Key, out var existing))
                        {
                            existing.Count += entry.Count;
                            if (entry.LastSeenUtc > existing.LastSeenUtc) existing.LastSeenUtc = entry.LastSeenUtc;
                            if (entry.WhenUtc < existing.WhenUtc) existing.WhenUtc = entry.WhenUtc;
                            continue;
                        }
                        entries.Add(entry);
                        byKey[entry.Key] = entry;
                    }
                    catch (Exception ex)
                    {
                        Log($"Corrupt rule-audit line skipped: {ex.Message}", LogLevel.Warning);
                    }
                }
                return entries;
            }
        }

        /// <summary>
        /// Records one thing a rule did. A repeat of the same (message, rule, action) bumps the
        /// count on the existing record; only a genuinely new event appends a line.
        /// </summary>
        public static void Record(RuleAuditEntry entry)
        {
            lock (gate)
            {
                var all = Entries;
                if (entry.LastSeenUtc == default) entry.LastSeenUtc = entry.WhenUtc;
                if (entry.Count < 1) entry.Count = 1;

                if (byKey!.TryGetValue(entry.Key, out var existing))
                {
                    existing.Count += entry.Count;
                    if (entry.LastSeenUtc > existing.LastSeenUtc) existing.LastSeenUtc = entry.LastSeenUtc;
                    Persist(all);
                    return;
                }

                all.Add(entry);
                byKey[entry.Key] = entry;

                if (all.Count > MaxEntries)
                {
                    foreach (var dropped in all.Take(all.Count - MaxEntries))
                        byKey.Remove(dropped.Key);
                    all.RemoveRange(0, all.Count - MaxEntries);
                    Persist(all);
                    return;
                }

                // The common case: one more line onto the end of the file instead of rewriting all
                // of it. The old code re-serialised half a megabyte for every single record.
                Append(entry);
            }
        }

        /// <summary>
        /// What rules did to this message, most recent first. Matched on Message-Id when there is
        /// one, so an entry survives the message being moved out of the folder it was found in.
        /// </summary>
        public static List<RuleAuditEntry> For(string accountId, string folderFullName, uint uid, string messageId)
        {
            lock (gate)
                return [.. Entries
                    .Where(e => e.AccountId == accountId
                        && (messageId.Length > 0 && e.MessageId == messageId
                            || e.FolderFullName == folderFullName && e.Uid == uid))
                    .OrderByDescending(static e => e.LastSeenUtc)];
        }

        public static IReadOnlyList<RuleAuditEntry> All
        {
            get { lock (gate) return [.. Entries]; }
        }

        static void Append(RuleAuditEntry entry)
        {
            try
            {
                File.AppendAllText(StorePath, JsonSerializer.Serialize(entry, JsonDefaults.SingleLine) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Log($"Could not append to the rule audit: {ex.Message}", LogLevel.Warning);
            }
        }

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
