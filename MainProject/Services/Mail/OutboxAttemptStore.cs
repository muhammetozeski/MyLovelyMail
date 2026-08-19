using System.Text;
using System.Text.Json;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>How a queued message is doing: how many sends were tried, what the last one said.</summary>
    public sealed class OutboxAttempt
    {
        public string AccountId { get; set; } = string.Empty;
        public uint Uid { get; set; }
        public int AttemptCount { get; set; }
        public DateTime LastAttemptUtc { get; set; }
        public string LastError { get; set; } = string.Empty;

        /// <summary>When the retries were stopped; null while it is still being tried.</summary>
        public DateTime? HeldUtc { get; set; }
    }

    /// <summary>
    /// The sending side's version of the bookkeeping <see cref="SyncHealthService"/> already keeps
    /// for receiving. A permanently misconfigured SMTP endpoint failed completely silently: the
    /// user got one notification at queue time promising the message "will be sent automatically",
    /// and after that every sync pass retried it and logged a warning nobody reads — no counter,
    /// no ceiling. The Outbox row carried no state at all, so a message queued thirty seconds ago
    /// looked exactly like one that had been failing for three days.
    /// </summary>
    public static class OutboxAttemptStore
    {
        public const string FileName = "outbox-attempts.jsonl";

        /// <summary>Failures before the message is held. Retrying past this is not persistence, it is spinning.</summary>
        public const int MaxAttempts = 5;

        static List<OutboxAttempt>? attempts;

        static string StorePath => Path.Combine(AppPaths.UserData, FileName);

        static List<OutboxAttempt> Attempts
        {
            get
            {
                if (attempts != null) return attempts;
                attempts = [];
                if (!File.Exists(StorePath)) return attempts;
                foreach (string line in File.ReadLines(StorePath))
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        if (JsonSerializer.Deserialize<OutboxAttempt>(line, JsonDefaults.SingleLine) is { } entry)
                            attempts.Add(entry);
                    }
                    catch (Exception ex)
                    {
                        Log($"Corrupt outbox-attempt line skipped: {ex.Message}", LogLevel.Warning);
                    }
                }
                return attempts;
            }
        }

        public static OutboxAttempt? For(string accountId, uint uid) =>
            Attempts.FirstOrDefault(a => a.AccountId == accountId && a.Uid == uid);

        public static IReadOnlyList<OutboxAttempt> All => Attempts;

        /// <summary>True when this message has been tried enough and is waiting for the user to say try again.</summary>
        public static bool IsHeld(string accountId, uint uid) => For(accountId, uid)?.HeldUtc != null;

        public static int HeldCount => Attempts.Count(static a => a.HeldUtc != null);

        public static void RecordFailure(string accountId, uint uid, string error)
        {
            var entry = For(accountId, uid);
            if (entry == null)
            {
                entry = new OutboxAttempt { AccountId = accountId, Uid = uid };
                Attempts.Add(entry);
            }
            entry.AttemptCount++;
            entry.LastAttemptUtc = DateTime.UtcNow;
            entry.LastError = error;
            if (entry.AttemptCount >= MaxAttempts && entry.HeldUtc == null)
            {
                entry.HeldUtc = DateTime.UtcNow;
                Log($"Outbox: uid {uid} held after {entry.AttemptCount} failed sends — {error}", LogLevel.Error);
            }
            Persist();
        }

        /// <summary>Forgets a message's history. Called when it leaves the outbox, sent or dropped — an entry must never outlive its message.</summary>
        public static void Clear(string accountId, uint uid)
        {
            if (Attempts.RemoveAll(a => a.AccountId == accountId && a.Uid == uid) > 0) Persist();
        }

        /// <summary>Releases every hold so the next flush tries again; the counts stay, as history.</summary>
        public static int ReleaseHolds()
        {
            int released = 0;
            foreach (var entry in Attempts.Where(static a => a.HeldUtc != null))
            {
                entry.HeldUtc = null;
                entry.AttemptCount = 0;
                released++;
            }
            if (released > 0) Persist();
            return released;
        }

        static void Persist()
        {
            try
            {
                var text = new StringBuilder();
                foreach (var entry in Attempts)
                    text.AppendLine(JsonSerializer.Serialize(entry, JsonDefaults.SingleLine));
                AtomicFile.WriteAllText(StorePath, text.ToString());
            }
            catch (Exception ex)
            {
                Log($"Could not write the outbox attempts: {ex.Message}", LogLevel.Warning);
            }
        }
    }
}
