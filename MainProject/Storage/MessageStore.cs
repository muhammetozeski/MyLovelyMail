using System.Text;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Storage
{
    /// <summary>Keeps summaries in memory and MIME bodies on disk. Server copies live under UserCache; local folders live under UserData. Each folder contains folder.json, index.jsonl and Messages/&lt;uid&gt;.eml.</summary>
    public static partial class MessageStore
    {
        public const string AccountsFolderName = "Accounts";
        public const string FoldersFolderName = "Folders";
        public const string MessagesFolderName = "Messages";
        public const string FolderInfoFileName = "folder.json";
        public const string IndexFileName = "index.jsonl";
        public const string MessageExtension = ".eml";

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

        /// <summary>Encodes server folder names for disk, including dot-only names and trailing dots or spaces that Windows normalizes. Leading dots remain valid, so existing Maildir caches keep their paths.</summary>
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
