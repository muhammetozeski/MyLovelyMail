using System.Collections.Concurrent;
using System.Text.Json;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>
    /// Holds every configured <see cref="MailAccountData"/> and its <see cref="AccountSettings"/>.
    /// Account definitions persist as <c>UserData/Accounts/&lt;id&gt;/account.json</c> — they are
    /// the user's own data, so unlike UserCache they are part of the migration bundle.
    /// </summary>
    public static class AccountStore
    {
        public const string AccountFileName = "account.json";

        static readonly List<MailAccountData> accounts = [];
        static readonly ConcurrentDictionary<string, AccountSettings> settingsById = [];

        public static event Action? OnAccountsChanged;

        public static IReadOnlyList<MailAccountData> Accounts => accounts;

        static string AccountFolder(string accountId) =>
            Path.Combine(AppPaths.UserData, AccountSettings.AccountsFolderName, accountId);

        /// <summary>Loads every account definition from disk. Called once at startup.</summary>
        public static void Load()
        {
            accounts.Clear();
            string accountsRoot = Path.Combine(AppPaths.UserData, AccountSettings.AccountsFolderName);
            if (!Directory.Exists(accountsRoot)) return;

            foreach (string dir in Directory.GetDirectories(accountsRoot))
            {
                string path = Path.Combine(dir, AccountFileName);
                if (!File.Exists(path)) continue;
                try
                {
                    var account = JsonSerializer.Deserialize<MailAccountData>(File.ReadAllText(path), JsonDefaults.Indented);
                    if (account != null) accounts.Add(account);
                }
                catch (Exception ex)
                {
                    Log($"Corrupt account file '{path}': {ex.Message}", LogLevel.Error);
                }
            }
            accounts.Sort(static (a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));
            Log($"AccountStore loaded: {accounts.Count} account(s).");
        }

        public static MailAccountData? GetById(string accountId) =>
            accounts.FirstOrDefault(a => a.Id == accountId);

        /// <summary>Adds a new account or persists changes of an existing one, then notifies the UI.</summary>
        public static void Save(MailAccountData account)
        {
            string dir = AccountFolder(account.Id);
            Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(Path.Combine(dir, AccountFileName), JsonSerializer.Serialize(account, JsonDefaults.Indented));

            if (!accounts.Any(a => a.Id == account.Id))
                accounts.Add(account);
            Log($"Account saved: {account.EmailAddress} ({account.Protocol} {account.IncomingHost})");
            OnAccountsChanged?.Invoke();
        }

        /// <summary>
        /// Removes the account definition (and its settings overrides) from UserData. The UserCache
        /// mail of the account is deleted separately so a mis-click never wipes gigabytes silently.
        /// </summary>
        public static void Remove(string accountId)
        {
            accounts.RemoveAll(a => a.Id == accountId);
            settingsById.TryRemove(accountId, out _);
            FolderMemoryStore.Forget(accountId);
            try
            {
                string dir = AccountFolder(accountId);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                Log($"Could not delete account folder for '{accountId}': {ex.Message}", LogLevel.Error);
            }
            OnAccountsChanged?.Invoke();
        }

        /// <summary>The account's settings (inheriting globals), loaded from disk on first access and cached.</summary>
        public static AccountSettings GetSettings(string accountId) =>
            settingsById.GetOrAdd(accountId, static id =>
            {
                var settings = new AccountSettings(id);
                settings.Load();
                return settings;
            });
    }
}
