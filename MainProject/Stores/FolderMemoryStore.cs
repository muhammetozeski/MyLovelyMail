using System.Text.Json;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>
    /// Remembers where the user was: the last folder opened per account, and which account was
    /// open last. Persisted as <c>UserData/folder-memory.json</c>.
    /// <para>
    /// Deliberately NOT stored on <see cref="MailAccountData"/>: <see cref="AccountStore.Save"/>
    /// logs and raises OnAccountsChanged, which the mail page turns into a full re-render — every
    /// folder click would cost a log line and a page refresh. This store writes a small file and
    /// tells nobody, because nothing needs to react to it.
    /// </para>
    /// <para>
    /// Loads itself on first use instead of being wired into the three startup paths (MauiProgram,
    /// Web Program, MigrationService), so there is no fourth place to forget.
    /// </para>
    /// </summary>
    public static class FolderMemoryStore
    {
        public const string FileName = "folder-memory.json";

        /// <summary>Key under which the last selected account id is kept; account ids are GUIDs, so it cannot collide.</summary>
        const string LastAccountKey = "$lastAccount";

        static Dictionary<string, string>? folderByAccount;

        static string MemoryPath => Path.Combine(AppPaths.UserData, FileName);

        static Dictionary<string, string> Memory
        {
            get
            {
                if (folderByAccount != null) return folderByAccount;
                folderByAccount = new(StringComparer.OrdinalIgnoreCase);
                if (!File.Exists(MemoryPath)) return folderByAccount;
                try
                {
                    if (JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(MemoryPath)) is { } loaded)
                        folderByAccount = new(loaded, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    Log($"Corrupt folder memory file, starting empty: {ex.Message}", LogLevel.Warning);
                }
                return folderByAccount;
            }
        }

        /// <summary>Folder full name this account was left in, or null when it has never been opened.</summary>
        public static string? FolderOf(string accountId) =>
            Memory.TryGetValue(accountId, out string? fullName) ? fullName : null;

        /// <summary>Account open when the app was last used, or null on a first run.</summary>
        public static string? LastAccountId =>
            Memory.TryGetValue(LastAccountKey, out string? accountId) ? accountId : null;

        /// <summary>
        /// Which folder an account should open in: the remembered one when it still exists in
        /// <paramref name="folders"/>, otherwise the Inbox, otherwise whatever is first. Resolving
        /// against the live list is what keeps a renamed or server-deleted folder from leaving the
        /// selection on a path the server no longer has — the caller would sync at nothing.
        /// </summary>
        /// <remarks>
        /// Takes the remembered name rather than the account id so a caller can hand it a name the
        /// server does not have and watch the fallback happen — the failure mode is the part worth
        /// checking, and it stays checkable without editing the saved file.
        /// </remarks>
        public static MailFolderData? ResolveStartFolder(string? rememberedFullName, IReadOnlyList<MailFolderData> folders)
        {
            var remembered = rememberedFullName is { Length: > 0 }
                ? folders.FirstOrDefault(f => f.FullName == rememberedFullName)
                : null;
            return remembered
                ?? folders.FirstOrDefault(static f => f.Role == FolderRole.Inbox)
                ?? folders.FirstOrDefault();
        }

        /// <summary>Records the folder as this account's place, and the account as the last one used.</summary>
        public static void Remember(string accountId, string folderFullName)
        {
            if (Memory.TryGetValue(accountId, out string? stored) && stored == folderFullName
                && LastAccountId == accountId) return;

            Memory[accountId] = folderFullName;
            Memory[LastAccountKey] = accountId;
            Persist();
        }

        /// <summary>Drops an account's place, called when the account itself is removed.</summary>
        public static void Forget(string accountId)
        {
            bool removed = Memory.Remove(accountId);
            if (LastAccountId == accountId) removed |= Memory.Remove(LastAccountKey);
            if (removed) Persist();
        }

        static void Persist()
        {
            try
            {
                AtomicFile.WriteAllText(MemoryPath, JsonSerializer.Serialize(Memory, JsonDefaults.Indented));
            }
            catch (Exception ex)
            {
                Log($"Could not save the folder memory: {ex.Message}", LogLevel.Warning);
            }
        }
    }
}
