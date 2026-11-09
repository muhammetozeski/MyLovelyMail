using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Storage
{
    public static partial class MessageStore
    {
        static readonly ConcurrentDictionary<string, FolderIndex> Indexes = [];

        sealed class FolderIndex
        {
            public readonly ConcurrentDictionary<uint, MailMessageSummary> Summaries = [];
            public readonly Lock SaveLock = new();
            public bool Loaded;
            public List<MailMessageSummary>? SortedCache;
            public Dictionary<string, int>? SenderCounts;

            public void InvalidateDerivedCaches()
            {
                SortedCache = null;
                SenderCounts = null;
            }
        }

        #region Summaries (the RAM mapping)

        static FolderIndex GetIndex(string accountId, string folderFullName)
        {
            var index = Indexes.GetOrAdd(FolderKey(accountId, folderFullName), static _ => new FolderIndex());
            if (!index.Loaded)
            {
                lock (index.SaveLock)
                {
                    if (!index.Loaded)
                    {
                        LoadIndexFromDisk(accountId, folderFullName, index);
                        index.Loaded = true;
                    }
                }
            }
            return index;
        }

        static void LoadIndexFromDisk(string accountId, string folderFullName, FolderIndex index)
        {
            string path = Path.Combine(FolderCachePath(accountId, folderFullName), IndexFileName);
            if (!File.Exists(path)) return;

            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var summary = JsonSerializer.Deserialize<MailMessageSummary>(line, JsonDefaults.SingleLine);
                    if (summary != null) index.Summaries[summary.Uid] = summary;
                }
                catch (Exception ex)
                {
                    Log($"Corrupt index line in '{path}': {ex.Message}", LogLevel.Warning);
                }
            }
            index.InvalidateDerivedCaches();
        }

        /// <summary>All summaries of a folder, newest first. Uses an in-memory sorted cache so repeated reads on large folders are O(1).</summary>
        public static List<MailMessageSummary> GetSummaries(string accountId, string folderFullName)
        {
            var index = GetIndex(accountId, folderFullName);
            var cached = index.SortedCache;
            if (cached != null) return [.. cached];

            lock (index.SaveLock)
            {
                cached = index.SortedCache ??= [.. index.Summaries.Values.OrderByDescending(static s => s.DateUtc)];
                return [.. cached];
            }
        }

        /// <summary>Counts cached summaries without sorting or building the displayed message list.</summary>
        /// <param name="accountId">Account owning the folder.</param>
        /// <param name="folderFullName">Server path or local folder name.</param>
        /// <param name="predicate">Optional condition, such as unread messages or a search query.</param>
        /// <returns>The number of matching summaries in the cached index.</returns>
        public static int CountSummaries(string accountId, string folderFullName,
            Func<MailMessageSummary, bool>? predicate = null)
        {
            var summaries = GetIndex(accountId, folderFullName).Summaries;
            return predicate == null ? summaries.Count : summaries.Values.Count(predicate);
        }

        /// <summary>
        /// Returns the total cached message count from <paramref name="senderAddress"/> across all folders of
        /// <paramref name="accountId"/> using a pre-aggregated O(1) dictionary per folder instead of scanning every message row.
        /// </summary>
        public static int GetSenderMessageCount(string accountId, string senderAddress)
        {
            if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(senderAddress)) return 0;
            int total = 0;
            foreach (var folder in GetFolders(accountId))
            {
                var index = GetIndex(accountId, folder.FullName);
                var counts = index.SenderCounts;
                if (counts == null)
                {
                    lock (index.SaveLock)
                    {
                        if (index.SenderCounts == null)
                        {
                            Dictionary<string, int> built = new(StringComparer.OrdinalIgnoreCase);
                            foreach (var summary in index.Summaries.Values)
                            {
                                if (string.IsNullOrEmpty(summary.FromAddress)) continue;
                                built[summary.FromAddress] = built.GetValueOrDefault(summary.FromAddress) + 1;
                            }
                            index.SenderCounts = built;
                        }
                        counts = index.SenderCounts;
                    }
                }
                if (counts.TryGetValue(senderAddress, out int folderCount))
                    total += folderCount;
            }
            return total;
        }

        public static MailMessageSummary? GetSummary(string accountId, string folderFullName, uint uid) =>
            GetIndex(accountId, folderFullName).Summaries.TryGetValue(uid, out var summary) ? summary : null;

        /// <summary>
        /// Messages whose local flag change is still on its way to the server, as
        /// <see cref="FolderKey"/> + uid. Kept in RAM only: it describes an in-flight push, and a
        /// push does not survive the process that started it.
        /// </summary>
        static readonly ConcurrentDictionary<string, byte> pendingFlagPushes = [];

        static string PendingKey(string accountId, string folderFullName, uint uid) =>
            FolderKey(accountId, folderFullName) + (char)31 + uid;

        /// <summary>Remembers that these messages carry a local flag change the server has not been told about yet.</summary>
        public static void MarkFlagPushPending(string accountId, string folderFullName, IEnumerable<uint> uids)
        {
            foreach (uint uid in uids) pendingFlagPushes[PendingKey(accountId, folderFullName, uid)] = 0;
        }

        /// <summary>The push landed (or gave up): the server's answer is authoritative again.</summary>
        public static void ClearFlagPushPending(string accountId, string folderFullName, IEnumerable<uint> uids)
        {
            foreach (uint uid in uids) pendingFlagPushes.TryRemove(PendingKey(accountId, folderFullName, uid), out _);
        }

        /// <summary>Flags the SERVER owns. Everything else in <see cref="MailFlags"/> exists only here.</summary>
        const MailFlags ServerOwnedFlags = MailFlags.Seen | MailFlags.Answered | MailFlags.Flagged | MailFlags.Deleted;

        /// <summary>Adds or replaces summaries, persists the index and notifies listeners.</summary>
        public static void UpsertSummaries(string accountId, string folderFullName, IEnumerable<MailMessageSummary> summaries)
        {
            var index = GetIndex(accountId, folderFullName);
            foreach (var summary in summaries)
            {
                if (index.Summaries.TryGetValue(summary.Uid, out var stored) && !ReferenceEquals(stored, summary))
                {
                    CarryOverLocalState(stored, summary);
                    if (pendingFlagPushes.ContainsKey(PendingKey(accountId, folderFullName, summary.Uid)))
                        summary.Flags = summary.Flags & ~ServerOwnedFlags | stored.Flags & ServerOwnedFlags;
                }
                index.Summaries[summary.Uid] = summary;
            }
            lock (index.SaveLock) index.InvalidateDerivedCaches();
            SaveIndex(accountId, folderFullName, index);
            RefreshUnreadCount(accountId, folderFullName, index);
            OnFolderChanged?.Invoke(accountId, folderFullName);
        }

        /// <summary>
        /// Tags, the Important/Muted markers and the snooze time exist ONLY here — no server knows
        /// them. A sync rebuilds summaries from the server and would silently wipe all of it, so a
        /// freshly built summary inherits the app-local state of the row it replaces.
        /// </summary>
        static void CarryOverLocalState(MailMessageSummary stored, MailMessageSummary incoming)
        {
            incoming.SnoozedUntilUtc ??= stored.SnoozedUntilUtc;
            if (incoming.Tags.Count == 0 && stored.Tags.Count > 0) incoming.Tags = stored.Tags;
            incoming.Flags |= stored.Flags & (MailFlags.Important | MailFlags.Muted);
        }

        /// <summary>
        /// Recomputes the folder badge from the cached summaries — correct ONLY while the cache
        /// holds the whole folder.
        /// </summary>
        static void RefreshUnreadCount(string accountId, string folderFullName, FolderIndex index)
        {
            var info = GetFolders(accountId).FirstOrDefault(f => f.FullName == folderFullName);
            if (info == null || index.Summaries.Count < info.TotalCount) return;
            int unread = index.Summaries.Values.Count(s => s.IsUnread);
            if (info.UnreadCount == unread) return;
            info.UnreadCount = unread;
            SaveFolder(info);
        }

        /// <summary>
        /// Flips the Seen flag on the given summaries and moves the folder badge by exactly the
        /// number that changed. Returns how many changed.
        /// </summary>
        public static int SetSeen(string accountId, string folderFullName, IEnumerable<MailMessageSummary> summaries, bool seen)
        {
            List<MailMessageSummary> changed = [.. summaries.Where(s => s.IsUnread == seen)];
            if (changed.Count == 0) return 0;

            var index = GetIndex(accountId, folderFullName);
            foreach (var summary in changed)
            {
                summary.Flags = seen ? summary.Flags | MailFlags.Seen : summary.Flags & ~MailFlags.Seen;
                index.Summaries[summary.Uid] = summary;
            }
            lock (index.SaveLock) index.InvalidateDerivedCaches();
            SaveIndex(accountId, folderFullName, index);

            if (GetFolders(accountId).FirstOrDefault(f => f.FullName == folderFullName) is { } info)
            {
                info.UnreadCount = Math.Max(0, info.UnreadCount + (seen ? -changed.Count : changed.Count));
                SaveFolder(info);
            }
            OnFolderChanged?.Invoke(accountId, folderFullName);
            return changed.Count;
        }

        /// <summary>Removes summaries and their cached .eml files, persists and notifies.</summary>
        public static void RemoveMessages(string accountId, string folderFullName, IEnumerable<uint> uids)
        {
            var index = GetIndex(accountId, folderFullName);
            int removedUnread = 0;
            foreach (uint uid in uids)
            {
                if (index.Summaries.TryRemove(uid, out var removed) && removed.IsUnread) removedUnread++;
                string path = MessagePath(accountId, folderFullName, uid);
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) { Log($"Could not delete cached message '{path}': {ex.Message}", LogLevel.Warning); }
            }
            lock (index.SaveLock) index.InvalidateDerivedCaches();
            SaveIndex(accountId, folderFullName, index);
            if (removedUnread > 0 && GetFolders(accountId).FirstOrDefault(f => f.FullName == folderFullName) is { } info)
            {
                info.UnreadCount = Math.Max(0, info.UnreadCount - removedUnread);
                SaveFolder(info);
            }
            OnFolderChanged?.Invoke(accountId, folderFullName);
        }

        static void SaveIndex(string accountId, string folderFullName, FolderIndex index)
        {
            string dir = FolderCachePath(accountId, folderFullName);
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            foreach (var summary in index.Summaries.Values)
                sb.AppendLine(JsonSerializer.Serialize(summary, JsonDefaults.SingleLine));

            lock (index.SaveLock)
                AtomicFile.WriteAllText(Path.Combine(dir, IndexFileName), sb.ToString());
        }

        #endregion
    }
}
