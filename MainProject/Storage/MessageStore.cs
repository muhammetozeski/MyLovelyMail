using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Storage
{
    /// <summary>
    /// Disk-backed mail storage. RAM holds ONLY the per-folder summary index
    /// (<see cref="MailMessageSummary"/>); full MIME bodies live as .eml files and are read from
    /// disk when a message is opened. Layout per folder:
    /// <c>&lt;root&gt;/Accounts/&lt;accountId&gt;/Folders/&lt;safeName&gt;/</c> holding
    /// <c>folder.json</c>, <c>index.jsonl</c> (one summary per line) and <c>Messages/&lt;uid&gt;.eml</c>.
    /// <para>
    /// The root is chosen by RECOVERABILITY, not by who wrote the folder. Mail mirrored from a
    /// server goes to <see cref="AppPaths.UserCache"/>, which stays safe to delete. Local folders —
    /// Drafts, Outbox, Sent and anything the user or a rule made — go to
    /// <see cref="AppPaths.UserData"/>, because no server has a copy to hand back.
    /// </para>
    /// <para>
    /// This class used to claim "deleting UserCache is always safe: everything here re-syncs from
    /// the server". That was true only while it held synced mail alone, and it stopped being true
    /// when local folders arrived — drafts and queued outgoing mail were living behind a name that
    /// promised the opposite. It cost one real bug already: the cache trimmer believed the sentence
    /// and had local folders in its deletion pool, so a draft could reopen empty and an Outbox
    /// message the user had already sent could be deleted before it went out.
    /// </para>
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

        /// <summary>Whether this folder exists only on this machine, and therefore cannot be re-fetched.</summary>
        public static bool IsLocalFolder(string folderFullName) =>
            folderFullName.StartsWith(LocalFolderPrefix, StringComparison.Ordinal);

        /// <summary>The two roots a folder can live under, newest-first for callers that scan both.</summary>
        public static IEnumerable<string> AccountRoots(string accountId) =>
        [
            Path.Combine(AppPaths.UserData, AccountsFolderName, accountId),
            Path.Combine(AppPaths.UserCache, AccountsFolderName, accountId)
        ];

        /// <summary>UserData for a folder with no server copy, UserCache for one that re-syncs.</summary>
        static string AccountRootFor(string accountId, string folderFullName) =>
            Path.Combine(IsLocalFolder(folderFullName) ? AppPaths.UserData : AppPaths.UserCache, AccountsFolderName, accountId);

        public static string FolderCachePath(string accountId, string folderFullName) =>
            Path.Combine(AccountRootFor(accountId, folderFullName), FoldersFolderName, ToSafeName(folderFullName));

        public static string MessagePath(string accountId, string folderFullName, uint uid) =>
            Path.Combine(FolderCachePath(accountId, folderFullName), MessagesFolderName, uid + MessageExtension);

        /// <summary>
        /// Turns an IMAP folder path into a valid folder name ("INBOX/Receipts" → "INBOX%2FReceipts").
        /// <para>
        /// The encoder is the one place a name chosen by a mail SERVER becomes a path, so the three
        /// spellings Windows does not treat as ordinary names are encoded rather than passed
        /// through. A folder named "." or ".." resolved to the account root, putting its files a
        /// level above Folders — inside the account's own cache, so contained, but not where the
        /// walk expects them. "INBOX." and "INBOX " both normalise to "INBOX" on Windows, so two
        /// distinct server folders shared one directory and overwrote each other's index.
        /// </para>
        /// <para>
        /// A LEADING dot is deliberately left alone: ".Sent" and ".Drafts" are ordinary names on
        /// Maildir-style servers, they are safe as directory names, and encoding them would move
        /// every such folder's cache for no gain.
        /// </para>
        /// </summary>
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

            string encoded = sb.ToString();

            // "." and ".." mean the current and parent directory, whatever else they are called.
            if (encoded.Length > 0 && encoded.All(static c => c == '.'))
                return string.Concat(encoded.Select(static _ => "%2E"));

            // Windows silently drops a trailing dot or space, which is how two different folders
            // ended up sharing one directory.
            int keep = encoded.Length;
            while (keep > 0 && (encoded[keep - 1] == '.' || encoded[keep - 1] == ' ')) keep--;
            if (keep == encoded.Length) return encoded;

            return encoded[..keep] + string.Concat(encoded[keep..].Select(static c => "%" + ((int)c).ToString("X2")));
        }

        /// <summary>Canonical account+folder key — the single place this pairing is ever built.</summary>
        public static string FolderKey(string accountId, string folderFullName) => accountId + '\u001F' + folderFullName;

        #endregion

        #region Folder info

        /// <summary>
        /// Reads every stored folder of the account (empty list when nothing is stored yet). Both
        /// roots are walked, because a folder's root depends on whether a server can hand it back.
        /// </summary>
        public static List<MailFolderData> GetFolders(string accountId)
        {
            List<MailFolderData> result = [];
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
                        if (folder != null && !result.Any(existing => existing.FullName == folder.FullName))
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
            try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path) : []; }
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

        /// <summary>Prefix marking folders that exist only on this machine (created by rules/user).</summary>
        public const string LocalFolderPrefix = "Local/";

        /// <summary>
        /// A uid the target local folder is not already using for a DIFFERENT message. A local
        /// folder has no server, so its uid is purely a local key and may be reassigned; the
        /// incoming one is kept when it is free or already belongs to this same message, so a
        /// repeated move is idempotent.
        /// </summary>
        static uint FreeLocalUid(string accountId, string targetFullName, MailMessageSummary summary)
        {
            var index = GetIndex(accountId, targetFullName);
            if (!index.Summaries.TryGetValue(summary.Uid, out var occupant) || SameMessage(occupant, summary))
                return summary.Uid;

            // Derived from the message's own identity, so the same message lands on the same key
            // every time; the walk only runs on the rare hash collision.
            uint candidate = StableHash.Fnv1a(summary.MessageId.Length > 0
                ? summary.MessageId
                : $"{summary.FromAddress}|{summary.Subject}|{summary.DateUtc:O}");
            while (index.Summaries.TryGetValue(candidate, out var taken) && !SameMessage(taken, summary))
                candidate++;

            Log($"Local folder '{targetFullName}' already used uid {summary.Uid}; filed this message under {candidate} instead.");
            return candidate;
        }

        /// <summary>Same mail, whatever uid it currently carries. Falls back to sender+subject+date when Message-Id is absent.</summary>
        static bool SameMessage(MailMessageSummary first, MailMessageSummary second) =>
            first.MessageId.Length > 0 || second.MessageId.Length > 0
                ? first.MessageId.Equals(second.MessageId, StringComparison.OrdinalIgnoreCase)
                : first.FromAddress == second.FromAddress && first.Subject == second.Subject && first.DateUtc == second.DateUtc;

        /// <summary>The one spelling of the inbox path: IMAP's mandated name and POP3's single mailbox mirror it.</summary>
        public const string InboxFullName = "INBOX";

        /// <summary>
        /// Moves one message into a local-only folder: creates the folder info on first use,
        /// carries the cached .eml along when present, and removes the source entry.
        /// <para>
        /// The uid is re-keyed on the way in. IMAP numbers restart per folder, so filing INBOX
        /// uid 7 and Sent uid 7 into the same local folder used to overwrite the .eml AND the
        /// index row — and CarryOverLocalState grafted the vanishing message's tags onto the
        /// survivor first, so the wreck looked like the message that had been destroyed. Every
        /// other collision in the app re-syncs away; this one has no server copy behind it.
        /// </para>
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
            summary.Uid = FreeLocalUid(accountId, targetFullName, summary);
            if (mimeBytes != null)
                SaveFullMessage(accountId, targetFullName, summary.Uid, mimeBytes);

            UpsertSummaries(accountId, targetFullName, [summary]);
            RemoveMessages(accountId, fromFolderFullName, [uid]);
        }

        /// <summary>
        /// Mirrors a move the SERVER already performed: the summary, its cached .eml and every
        /// app-local field (tags, important, snooze, mute) land in the destination folder under the
        /// uid the server assigned there, and the source row goes away.
        /// <para>
        /// Only ever called with a destination uid the server reported (UIDPLUS). Inventing one
        /// would repeat the collision local folders already taught: uids restart per folder, so a
        /// guessed number can land on top of a message that is already there.
        /// </para>
        /// </summary>
        public static void MoveToServerFolder(string accountId, string fromFolderFullName, uint uid, string toFolderFullName, uint newUid)
        {
            var summary = GetSummary(accountId, fromFolderFullName, uid);
            if (summary == null) return;

            byte[]? mimeBytes = TryLoadFullMessage(accountId, fromFolderFullName, uid);
            summary.Uid = newUid;
            if (mimeBytes != null)
                SaveFullMessage(accountId, toFolderFullName, newUid, mimeBytes);

            UpsertSummaries(accountId, toFolderFullName, [summary]);
            RemoveMessages(accountId, fromFolderFullName, [uid]);
        }

        #region Full message bodies (disk only)

        public static bool HasFullMessage(string accountId, string folderFullName, uint uid) =>
            File.Exists(MessagePath(accountId, folderFullName, uid));

        public static void SaveFullMessage(string accountId, string folderFullName, uint uid, byte[] mimeBytes)
        {
            string path = MessagePath(accountId, folderFullName, uid);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Plain File.WriteAllBytes truncates before it writes, and this runs on every body
            // fetch — a 50-message slice is 50 chances for a tray Exit or a shutdown to leave a
            // zero-byte .eml behind, which HasFullMessage then reports as a cached body forever.
            AtomicFile.WriteAllBytes(path, mimeBytes);
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
