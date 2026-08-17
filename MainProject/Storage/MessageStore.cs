using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyLovelyMail.MainProject.DataModels.Mail;

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

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            Converters = { new JsonStringEnumConverter() },
            WriteIndented = true
        };

        static readonly JsonSerializerOptions IndexLineOptions = new()
        {
            Converters = { new JsonStringEnumConverter() }
        };

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

        static string IndexKey(string accountId, string folderFullName) => accountId + '\u001F' + folderFullName;

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
                    var folder = JsonSerializer.Deserialize<MailFolderData>(File.ReadAllText(infoPath), JsonOptions);
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
            AtomicWrite(Path.Combine(dir, FolderInfoFileName), JsonSerializer.Serialize(folder, JsonOptions));
        }

        #endregion

        #region Summaries (the RAM mapping)

        static FolderIndex GetIndex(string accountId, string folderFullName)
        {
            var index = Indexes.GetOrAdd(IndexKey(accountId, folderFullName), static _ => new FolderIndex());
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
                    var summary = JsonSerializer.Deserialize<MailMessageSummary>(line, IndexLineOptions);
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
                index.Summaries[summary.Uid] = summary;
            SaveIndex(accountId, folderFullName, index);
            RefreshUnreadCount(accountId, folderFullName, index);
            OnFolderChanged?.Invoke(accountId, folderFullName);
        }

        /// <summary>Keeps the folder's unread badge honest after local flag changes (sync overwrites with server truth later).</summary>
        static void RefreshUnreadCount(string accountId, string folderFullName, FolderIndex index)
        {
            var info = GetFolders(accountId).FirstOrDefault(f => f.FullName == folderFullName);
            if (info == null) return;
            int unread = index.Summaries.Values.Count(s => s.IsUnread);
            if (info.UnreadCount == unread) return;
            info.UnreadCount = unread;
            SaveFolder(info);
        }

        /// <summary>Removes summaries and their cached .eml files, persists and notifies.</summary>
        public static void RemoveMessages(string accountId, string folderFullName, IEnumerable<uint> uids)
        {
            var index = GetIndex(accountId, folderFullName);
            foreach (uint uid in uids)
            {
                index.Summaries.TryRemove(uid, out _);
                string path = MessagePath(accountId, folderFullName, uid);
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) { Log($"Could not delete cached message '{path}': {ex.Message}", LogLevel.Warning); }
            }
            SaveIndex(accountId, folderFullName, index);
            OnFolderChanged?.Invoke(accountId, folderFullName);
        }

        static void SaveIndex(string accountId, string folderFullName, FolderIndex index)
        {
            string dir = FolderCachePath(accountId, folderFullName);
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            foreach (var summary in index.Summaries.Values)
                sb.AppendLine(JsonSerializer.Serialize(summary, IndexLineOptions));

            lock (index.SaveLock)
                AtomicWrite(Path.Combine(dir, IndexFileName), sb.ToString());
        }

        #endregion

        /// <summary>Prefix marking folders that exist only on this machine (created by rules/user).</summary>
        public const string LocalFolderPrefix = "Local/";

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

        #endregion

        static void AtomicWrite(string path, string content) => Stores.AtomicFile.WriteAllText(path, content);
    }
}
