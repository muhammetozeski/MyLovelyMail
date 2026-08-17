using System.IO.Compression;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>
    /// One-file machine migration: zips the user's own data (config, accounts, filters, tags —
    /// everything under UserData except the machine-bound vault) together with a
    /// passphrase-encrypted portable copy of the credential vault. Importing restores the files
    /// and merges the vault, then reloads every store, so the target machine only re-syncs mail
    /// (UserCache is intentionally NOT bundled — it re-downloads).
    /// </summary>
    public static class MigrationService
    {
        public const string BundleExtension = ".lovelybundle";
        const string PortableVaultEntryName = "portable-vault.json";

        /// <summary>Writes the bundle to <paramref name="bundlePath"/> (extension added when missing).</summary>
        public static string ExportBundle(string bundlePath, string passphrase)
        {
            if (!bundlePath.EndsWith(BundleExtension, StringComparison.OrdinalIgnoreCase))
                bundlePath += BundleExtension;

            string temporaryVault = Path.Combine(AppPaths.AppCache, PortableVaultEntryName);
            CredentialVault.ExportPortable(temporaryVault, passphrase);
            try
            {
                if (File.Exists(bundlePath)) File.Delete(bundlePath);
                using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Create);

                foreach (string file in Directory.GetFiles(AppPaths.UserData, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(AppPaths.UserData, file);
                    // The machine-bound vault never travels; the portable copy replaces it.
                    if (relative.Equals(CredentialVault.VaultFileName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    archive.CreateEntryFromFile(file, relative.Replace('\\', '/'));
                }
                archive.CreateEntryFromFile(temporaryVault, PortableVaultEntryName);
            }
            finally
            {
                File.Delete(temporaryVault);
            }
            return bundlePath;
        }

        /// <summary>Restores a bundle into UserData and reloads every store. False on a wrong passphrase.</summary>
        public static bool ImportBundle(string bundlePath, string passphrase)
        {
            string temporaryVault = Path.Combine(AppPaths.AppCache, PortableVaultEntryName);
            using (var archive = ZipFile.OpenRead(bundlePath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith('/')) continue;

                    if (entry.Name.Equals(PortableVaultEntryName, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.ExtractToFile(temporaryVault, overwrite: true);
                        continue;
                    }

                    string target = Path.Combine(AppPaths.UserData, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }

            SettingsManager.LoadSettings();
            AccountStore.Load();
            FilterRuleStore.Load();
            TagStore.Load();
            CredentialVault.Load();

            bool vaultMerged = File.Exists(temporaryVault) && CredentialVault.ImportPortable(temporaryVault, passphrase);
            if (File.Exists(temporaryVault)) File.Delete(temporaryVault);

            Events.MainEvents.Trigger("OnThemeChanged");
            return vaultMerged;
        }
    }
}
