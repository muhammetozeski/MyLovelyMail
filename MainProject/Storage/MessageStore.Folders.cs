using System.Collections.Concurrent;
using System.Text.Json;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Storage
{
    public static partial class MessageStore
    {
        #region Folder info

        static readonly ConcurrentDictionary<string, List<MailFolderData>> FolderCache = new(StringComparer.Ordinal);
        static readonly Lock FolderCacheLock = new();

        /// <summary>
        /// Reads every stored folder of the account (empty list when nothing is stored yet), backed by an
        /// in-memory cache so UI renders and sync status updates do not re-read and deserialize folder.json files from disk.
        /// </summary>
        public static List<MailFolderData> GetFolders(string accountId)
        {
            if (string.IsNullOrEmpty(accountId)) return [];
            if (FolderCache.TryGetValue(accountId, out var cached))
            {
                lock (FolderCacheLock)
                    return [.. cached];
            }

            List<MailFolderData> loaded = LoadFoldersFromDisk(accountId);
            lock (FolderCacheLock)
            {
                FolderCache[accountId] = loaded;
                return [.. loaded];
            }
        }

        static List<MailFolderData> LoadFoldersFromDisk(string accountId)
        {
            List<MailFolderData> result = [];
            HashSet<string> seenNames = new(StringComparer.Ordinal);
            foreach (string root in AccountRoots(accountId))
            {
                string foldersRoot = Path.Combine(root, FoldersFolderName);
                if (!Directory.Exists(foldersRoot)) continue;

                foreach (string dir in Directory.GetDirectories(foldersRoot))
                {
                    string infoPath = Path.Combine(dir, FolderInfoFileName);
                    if (!File.Exists(infoPath)) continue;
                    try
                    {
                        var folder = JsonSerializer.Deserialize<MailFolderData>(File.ReadAllText(infoPath), JsonDefaults.Indented);
                        if (folder != null && seenNames.Add(folder.FullName))
                            result.Add(folder);
                    }
                    catch (Exception ex)
                    {
                        Log($"Corrupt folder info '{infoPath}': {ex.Message}", LogLevel.Warning);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Moves local folders that earlier versions wrote into UserCache over to UserData, once.
        /// Runs at startup before anything reads a folder; a folder already at its destination or
        /// a move that fails is left where it is, so a half-done migration still reads correctly
        /// (<see cref="GetFolders"/> walks both roots).
        /// </summary>
        public static int MoveLocalFoldersOutOfCache()
        {
            string cacheAccounts = Path.Combine(AppPaths.UserCache, AccountsFolderName);
            if (!Directory.Exists(cacheAccounts)) return 0;

            int moved = 0;
            foreach (string accountDir in SafeDirectories(cacheAccounts))
            {
                string accountId = Path.GetFileName(accountDir);
                foreach (string folderDir in SafeDirectories(Path.Combine(accountDir, FoldersFolderName)))
                {
                    string decoded = Path.GetFileName(folderDir);
                    if (!decoded.StartsWith(ToSafeName(LocalFolderPrefix), StringComparison.OrdinalIgnoreCase)) continue;

                    string destination = Path.Combine(AppPaths.UserData, AccountsFolderName, accountId, FoldersFolderName, decoded);
                    if (Directory.Exists(destination)) continue;

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        Directory.Move(folderDir, destination);
                        moved++;
                        Log($"Local folder '{decoded}' moved out of the cache into UserData.");
                    }
                    catch (Exception ex)
                    {
                        Log($"Could not move local folder '{folderDir}' into UserData: {ex.Message}", LogLevel.Warning);
                    }
                }
                FolderCache.TryRemove(accountId, out _);
            }
            if (moved > 0) Log($"Storage migration: {moved} local folder(s) now live in UserData.");
            return moved;
        }

        static IEnumerable<string> SafeDirectories(string path)
        {
            try { return Directory.Exists(path) ? Directory.GetDirectories(path) : []; }
            catch (Exception ex)
            {
                Log($"Could not list '{path}': {ex.Message}", LogLevel.Warning);
                return [];
            }
        }

        public static void SaveFolder(MailFolderData folder)
        {
            string dir = FolderCachePath(folder.AccountId, folder.FullName);
            Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(Path.Combine(dir, FolderInfoFileName), JsonSerializer.Serialize(folder, JsonDefaults.Indented));

            lock (FolderCacheLock)
            {
                if (FolderCache.TryGetValue(folder.AccountId, out var list))
                {
                    int idx = list.FindIndex(f => string.Equals(f.FullName, folder.FullName, StringComparison.Ordinal));
                    if (idx >= 0) list[idx] = folder;
                    else list.Add(folder);
                }
            }
        }

        /// <summary>Toggles and persists whether a mail folder's children are expanded in the sidebar tree.</summary>
        public static void ToggleFolderExpanded(string accountId, string folderFullName)
        {
            var folders = GetFolders(accountId);
            var target = folders.FirstOrDefault(f => string.Equals(f.FullName, folderFullName, StringComparison.Ordinal));
            if (target == null) return;
            target.IsExpanded = !target.IsExpanded;
            SaveFolder(target);
            OnFolderChanged?.Invoke(accountId, folderFullName);
        }

        /// <summary>Reorders a mail folder before or after another mail folder in the sidebar and persists the order across restarts.</summary>
        public static void ReorderFolder(string accountId, string draggedFullName, string targetFullName, bool insertAfter = false)
        {
            if (string.IsNullOrWhiteSpace(draggedFullName) || string.IsNullOrWhiteSpace(targetFullName)
                || string.Equals(draggedFullName, targetFullName, StringComparison.Ordinal))
                return;

            var allFolders = GetFolders(accountId);
            var ordered = FolderTreeService.Build(allFolders).Select(static n => n.Folder).ToList();
            var dragged = ordered.FirstOrDefault(f => string.Equals(f.FullName, draggedFullName, StringComparison.Ordinal));
            var target = ordered.FirstOrDefault(f => string.Equals(f.FullName, targetFullName, StringComparison.Ordinal));
            if (dragged == null || target == null) return;

            ordered.Remove(dragged);
            int targetIdx = ordered.IndexOf(target);
            if (targetIdx < 0) return;
            if (insertAfter) targetIdx++;
            ordered.Insert(Math.Clamp(targetIdx, 0, ordered.Count), dragged);

            for (int i = 0; i < ordered.Count; i++)
            {
                int desiredOrder = i + 1;
                if (ordered[i].SortOrder != desiredOrder)
                {
                    ordered[i].SortOrder = desiredOrder;
                    SaveFolder(ordered[i]);
                }
            }
            OnFolderChanged?.Invoke(accountId, draggedFullName);
        }

        /// <summary>
        /// Forgets a folder completely: its cache, its info file and its directory. Only for a
        /// folder the SERVER no longer has — a cached folder that outlives its server copy keeps
        /// advertising a count nothing can refresh and stays a target in the move menu.
        /// </summary>
        public static void RemoveFolder(string accountId, string folderFullName)
        {
            Indexes.TryRemove(FolderKey(accountId, folderFullName), out _);
            lock (FolderCacheLock)
            {
                if (FolderCache.TryGetValue(accountId, out var list))
                    list.RemoveAll(f => string.Equals(f.FullName, folderFullName, StringComparison.Ordinal));
            }
            try
            {
                string dir = FolderCachePath(accountId, folderFullName);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                Log($"Could not remove the cache of '{folderFullName}': {ex.Message}", LogLevel.Warning);
            }
            OnFolderChanged?.Invoke(accountId, folderFullName);
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
    }
}
