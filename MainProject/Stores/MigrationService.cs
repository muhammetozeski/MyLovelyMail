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
                    // The machine-bound vault never travels; the portable copy replaces it. Nor
                    // does its entropy — and that one matters in the other direction: importing it
                    // would REPLACE the entropy on the target machine, leaving whatever vault that
                    // machine already had protected with a value no longer on disk. The portable
                    // vault carries the secrets, so nothing is lost by leaving it behind.
                    if (relative.Equals(CredentialVault.VaultFileName, StringComparison.OrdinalIgnoreCase)
                        || relative.Equals(CredentialVault.DpapiEntropyFileName, StringComparison.OrdinalIgnoreCase))
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

        /// <summary>
        /// Where an archive entry is allowed to land, or null when it is not allowed at all.
        /// <para>
        /// A zip entry name is attacker-controlled text, and <see cref="Path.Combine"/> honours the
        /// <c>..</c> segments in it: an entry called
        /// <c>../../../../AppData/Roaming/Microsoft/Windows/Start Menu/Programs/Startup/x.cmd</c>
        /// used to be written exactly there, which is code running at the next sign-in. The full
        /// resolved path has to sit under the destination, and the trailing separator matters —
        /// without it a sibling folder whose name merely starts with the same letters would pass.
        /// </para>
        /// </summary>
        static string? ContainedTarget(string root, string entryName)
        {
            string boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target;
            try
            {
                target = Path.GetFullPath(Path.Combine(root, entryName));
            }
            catch
            {
                // A name with characters this platform cannot express in a path is not one we place.
                return null;
            }
            return target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ? target : null;
        }

        /// <summary>
        /// Restores a bundle into UserData and reloads every store. False on a wrong passphrase.
        /// <para>
        /// Every entry is checked BEFORE anything is written, and one bad entry refuses the whole
        /// bundle: an archive that tries to escape is hostile, and importing the acceptable half of
        /// it would leave the user with a store they did not choose and no way to tell which parts
        /// arrived.
        /// </para>
        /// </summary>
        public static bool ImportBundle(string bundlePath, string passphrase)
        {
            string temporaryVault = Path.Combine(AppPaths.AppCache, PortableVaultEntryName);
            using (var archive = ZipFile.OpenRead(bundlePath))
            {
                var planned = new List<(ZipArchiveEntry Entry, string Target)>();
                ZipArchiveEntry? vaultEntry = null;

                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith('/')) continue;

                    if (entry.Name.Equals(PortableVaultEntryName, StringComparison.OrdinalIgnoreCase))
                    {
                        vaultEntry = entry;
                        continue;
                    }

                    string target = ContainedTarget(AppPaths.UserData, entry.FullName)
                        ?? throw new InvalidOperationException(
                            $"This bundle was refused: the entry '{entry.FullName}' points outside the data folder. Nothing was imported.");
                    planned.Add((entry, target));
                }

                vaultEntry?.ExtractToFile(temporaryVault, overwrite: true);
                foreach (var (entry, target) in planned)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
                Log($"Migration bundle imported: {planned.Count} file(s) into UserData.");
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
