using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Storage
{
    /// <summary>
    /// Disk-backed mail storage under <see cref="AppPaths.UserCache"/>. RAM holds ONLY the
    /// per-folder summary index (<see cref="MailMessageSummary"/>); full MIME bodies live as
    /// .eml files and are read from disk when a message is opened. Layout per folder:
    /// <c>UserCache/Accounts/&lt;accountId&gt;/Folders/&lt;safeName&gt;/</c> holding
    /// <c>folder.json</c>, <c>index.jsonl</c> (one summary per line) and <c>Messages/&lt;uid&gt;.eml</c>.
    /// Deleting UserCache is always safe: everything here re-syncs from the server.
    /// </summary>
    public static class MessageStore
    {
        public const string AccountsFolderName = "Accounts";
        public const string FoldersFolderName = "Folders";
        public const string MessagesFolderName = "Messages";
        public const string FolderInfoFileName = "folder.json";
        public const string IndexFileName = "index.jsonl";
        public const string MessageExtension = ".eml";

        sealed class FolderIndex
        {
            public readonly ConcurrentDictionary<uint, MailMessageSummary> Summaries = [];
            public readonly Lock SaveLock = new();
            public bool Loaded;
        }

        static readonly ConcurrentDictionary<string, FolderIndex> Indexes = [];

        /// <summary>Raised after summaries of (accountId, folderFullName) changed, so open lists can refresh.</summary>
        public static event Action<string, string>? OnFolderChanged;

        #region Paths

        static string AccountCacheFolder(string accountId) =>
            Path.Combine(AppPaths.UserCache, AccountsFolderName, accountId);

        public static string FolderCachePath(string accountId, string folderFullName) =>
            Path.Combine(AccountCacheFolder(accountId), FoldersFolderName, ToSafeName(folderFullName));

        public static string MessagePath(string accountId, string folderFullName, uint uid) =>
            Path.Combine(FolderCachePath(accountId, folderFullName), MessagesFolderName, uid + MessageExtension);

        /// <summary>Turns an IMAP folder path into a valid folder name ("INBOX/Receipts" → "INBOX%2FReceipts").</summary>
        internal static string ToSafeName(string folderFullName)
        {
            var sb = new StringBuilder(folderFullName.Length);
            foreach (char c in folderFullName)
            {
                if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ')
                    sb.Append(c);
                else
                    sb.Append('%').Append(((int)c).ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>Canonical account+folder key — the single place this pairing is ever built.</summary>
        public static string FolderKey(string accountId, string folderFullName) => accountId + '\u001F' + folderFullName;

        #endregion

        #region Folder info

        /// <summary>Reads every cached folder of the account (empty list when nothing is cached yet).</summary>
        public static List<MailFolderData> GetFolders(string accountId)
        {
            string foldersRoot = Path.Combine(AccountCacheFolder(accountId), FoldersFolderName);
            List<MailFolderData> result = [];
            if (!Directory.Exists(foldersRoot)) return result;

            foreach (string dir in Directory.GetDirectories(foldersRoot))
            {
                string infoPath = Path.Combine(dir, FolderInfoFileName);
                if (!File.Exists(infoPath)) continue;
                try
                {
                    var folder = JsonSerializer.Deserialize<MailFolderData>(File.ReadAllText(infoPath), JsonDefaults.Indented);
                    if (folder != null) result.Add(folder);
                }
                catch (Exception ex)
                {
                    Log($"Corrupt folder info '{infoPath}': {ex.Message}", LogLevel.Warning);
                }
            }
            return result;
        }

        public static void SaveFolder(MailFolderData folder)
        {
            string dir = FolderCachePath(folder.AccountId, folder.FullName);
            Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(Path.Combine(dir, FolderInfoFileName), JsonSerializer.Serialize(folder, JsonDefaults.Indented));
        }

        /// <summary>
        /// Throws the folder's cache away — RAM index, index.jsonl and the cached .eml files — and
        /// rewinds LastSeenUid so the next sync refills from scratch. UidValidity is KEPT: the
        /// sync's invalidation branch must stay quiet so the plain first-fill path runs.
        /// Dropping the RAM entry is mandatory, since a loaded index would otherwise survive the
        /// file deletion and keep serving stale summaries.
        /// </summary>
        public static void ClearFolderCache(string accountId, string folderFullName)
        {
            Indexes.TryRemove(FolderKey(accountId, folderFullName), out _);

            string dir = FolderCachePath(accountId, folderFullName);
            try
            {
                string indexPath = Path.Combine(dir, IndexFileName);
                if (File.Exists(indexPath)) File.Delete(indexPath);
                string messagesDir = Path.Combine(dir, MessagesFolderName);
                if (Directory.Exists(messagesDir)) Directory.Delete(messagesDir, recursive: true);
            }
            catch (Exception ex)
            {
                Log($"Could not clear the cache of '{folderFullName}': {ex.Message}", LogLevel.Warning);
            }

            if (GetFolders(accountId).FirstOrDefault(f => f.FullName == folderFullName) is { } folder)
            {
                folder.LastSeenUid = 0;
                folder.UnreadCount = 0;
                folder.TotalCount = 0;
                SaveFolder(folder);
            }
            Log($"Folder cache cleared for '{folderFullName}'; next sync refills it.");
            OnFolderChanged?.Invoke(accountId, folderFullName);
        }

        #endregion

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

        /// <summary>Adds or replaces summaries, persists the index and notifies listeners.</summary>
        public static void UpsertSummaries(string accountId, string folderFullName, IEnumerable<MailMessageSummary> summaries)
        {
            var index = GetIndex(accountId, folderFullName);
            foreach (var summary in summaries)
            {
                if (index.Summaries.TryGetValue(summary.Uid, out var stored) && !ReferenceEquals(stored, summary))
                    CarryOverLocalState(stored, summary);
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

        /// <summary>Prefix marking folders that exist only on this machine (created by rules/user).</summary>
        public const string LocalFolderPrefix = "Local/";

        /// <summary>The one spelling of the inbox path: IMAP's mandated name and POP3's single mailbox mirror it.</summary>
        public const string InboxFullName = "INBOX";

        /// <summary>
        /// Moves one message into a local-only folder: creates the folder info on first use,
        /// carries the cached .eml along when present, and removes the source entry.
        /// </summary>
        public static void MoveToLocalFolder(string accountId, string fromFolderFullName, uint uid, string localFolderName)
        {
            var summary = GetSummary(accountId, fromFolderFullName, uid);
            if (summary == null) return;

            string targetFullName = LocalFolderPrefix + localFolderName;
            if (!GetFolders(accountId).Any(f => f.FullName == targetFullName))
                SaveFolder(new MailFolderData
                {
                    AccountId = accountId,
                    FullName = targetFullName,
                    DisplayName = localFolderName,
                    IsLocal = true
                });

            byte[]? mimeBytes = TryLoadFullMessage(accountId, fromFolderFullName, uid);
            if (mimeBytes != null)
                SaveFullMessage(accountId, targetFullName, uid, mimeBytes);

            UpsertSummaries(accountId, targetFullName, [summary]);
            RemoveMessages(accountId, fromFolderFullName, [uid]);
        }

        #region Full message bodies (disk only)

        public static bool HasFullMessage(string accountId, string folderFullName, uint uid) =>
            File.Exists(MessagePath(accountId, folderFullName, uid));

        public static void SaveFullMessage(string accountId, string folderFullName, uint uid, byte[] mimeBytes)
        {
            string path = MessagePath(accountId, folderFullName, uid);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, mimeBytes);
        }

        /// <summary>The raw MIME of a message, or null when it is not cached yet (caller then fetches it).</summary>
        public static byte[]? TryLoadFullMessage(string accountId, string folderFullName, uint uid)
        {
            string path = MessagePath(accountId, folderFullName, uid);
            try
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception ex)
            {
                Log($"Could not read cached message '{path}': {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        /// <summary>
        /// Deletes only the cached body of a message, leaving its summary in the list — the row
        /// stays, and opening it downloads the body again. True when a file was actually removed.
        /// </summary>
        public static bool DeleteCachedBody(string accountId, string folderFullName, uint uid)
        {
            string path = MessagePath(accountId, folderFullName, uid);
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Could not drop cached body '{path}': {ex.Message}", LogLevel.Warning);
                return false;
            }
        }

        /// <summary>The cached MIME parsed, or null when not cached. Parse failures throw — callers decide how to handle them.</summary>
        public static MimeMessage? TryLoadMimeMessage(string accountId, string folderFullName, uint uid)
        {
            byte[]? bytes = TryLoadFullMessage(accountId, folderFullName, uid);
            if (bytes == null) return null;
            using var stream = new MemoryStream(bytes);
            return MimeMessage.Load(stream);
        }

        #endregion
    }
}
