using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Storage
{
    public static partial class MessageStore
    {
        sealed class FolderIndex
        {
            public readonly ConcurrentDictionary<uint, MailMessageSummary> Summaries = [];
            public readonly Lock SaveLock = new();
            public bool Loaded;
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
        }

        /// <summary>All summaries of a folder, newest first. Loads the index from disk on first access.</summary>
        public static List<MailMessageSummary> GetSummaries(string accountId, string folderFullName) =>
            [.. GetIndex(accountId, folderFullName).Summaries.Values.OrderByDescending(s => s.DateUtc)];

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
                    // A read/star the user just made, whose push has not landed: the server is
                    // reporting the state from BEFORE the change, so letting its answer win would
                    // flip the message back in front of the user who just changed it.
                    if (pendingFlagPushes.ContainsKey(PendingKey(accountId, folderFullName, summary.Uid)))
                        summary.Flags = summary.Flags & ~ServerOwnedFlags | stored.Flags & ServerOwnedFlags;
                }
                index.Summaries[summary.Uid] = summary;
            }
            SaveIndex(accountId, folderFullName, index);
            RefreshUnreadCount(accountId, folderFullName, index);
            OnFolderChanged?.Invoke(accountId, folderFullName);
        }

        /// <summary>
        /// Tags, the Important/Muted markers and the snooze time exist ONLY here — no server knows
        /// them. A sync rebuilds summaries from the server and would silently wipe all of it, so a
        /// freshly built summary inherits the app-local state of the row it replaces. Server-owned
        /// fields (Seen, Flagged, subject, dates) keep coming from the incoming copy.
        /// The app's own edits mutate the stored instance itself, and that case is skipped by the
        /// caller's reference check — otherwise clearing a marker would immediately undo itself.
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
        /// <para>
        /// The badge is the server's unread number for the entire folder, but the cache may hold a
        /// slice of it (313 of 9,624 after a first fill). Recomputing from that slice dropped the
        /// Inbox badge from 8,607 to 149 the moment anything touched a flag, and the next sync put
        /// it back — a badge that meant two different things depending on what ran last.
        /// Truncated folders take a delta from <see cref="SetSeen"/> instead.
        /// </para>
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
        /// number that changed. The store owns this because it owns the badge: callers that
        /// mutated <c>Flags</c> themselves and then upserted left no way to tell what changed,
        /// which is why the count had to be guessed from the cache. Returns how many changed.
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
            SaveIndex(accountId, folderFullName, index);
            // Same reason as SetSeen: a truncated folder's badge cannot be recomputed from the
            // slice in the cache, so it moves by exactly what left.
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
