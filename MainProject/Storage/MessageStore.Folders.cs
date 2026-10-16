using System.Text.Json;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Storage
{
    public static partial class MessageStore
    {
        #region Folder info

        /// <summary>
        /// Reads every stored folder of the account (empty list when nothing is stored yet). Both
        /// roots are walked, because a folder's root depends on whether a server can hand it back.
        /// </summary>
        public static List<MailFolderData> GetFolders(string accountId)
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
                        // A folder is claimed by exactly one root; a copy left in the other by an
                        // interrupted migration must not appear twice in the list.
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
                    // The directory name is the encoded folder path, so "Local/..." is readable
                    // from it without opening folder.json — and a folder whose info file is
                    // missing or broken still gets moved rather than being stranded in the cache.
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
        }

        /// <summary>
        /// Forgets a folder completely: its cache, its info file and its directory. Only for a
        /// folder the SERVER no longer has — a cached folder that outlives its server copy keeps
        /// advertising a count nothing can refresh and stays a target in the move menu.
        /// </summary>
        public static void RemoveFolder(string accountId, string folderFullName)
        {
            Indexes.TryRemove(FolderKey(accountId, folderFullName), out _);
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
