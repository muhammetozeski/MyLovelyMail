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

        /// <summary>
        /// First bytes of an encrypted bundle, followed by the PBKDF2 salt and the AES-GCM blob.
        /// A file without it is a bundle from before the whole archive was encrypted, and is still
        /// read — refusing to import someone's own backup would be a worse bug than the one this
        /// header fixes.
        /// </summary>
        static readonly byte[] EncryptedMagic = "MLMBUNDLE1\n"u8.ToArray();

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

                // Built in memory and encrypted as a whole. It used to be written as a plain zip
                // with one encrypted entry inside it, so the passphrase protected the PASSWORDS
                // and nothing else: anyone holding the file — it gets emailed, left on a USB stick,
                // synced to cloud backup — opened it with any unzip tool and read every address,
                // every server, every port and every filter rule. For a Tor-only account that file
                // named the provider the account exists to keep private.
                using var buffer = new MemoryStream();
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    WriteEntries(archive, temporaryVault);
                }

                byte[] salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(CredentialVault.SaltBytes);
                byte[] sealedBytes = CredentialVault.EncryptAesGcm(buffer.ToArray(), CredentialVault.DeriveKey(passphrase, salt));

                using var output = File.Create(bundlePath);
                output.Write(EncryptedMagic);
                output.Write(salt);
                output.Write(sealedBytes);
            }
            finally
            {
                File.Delete(temporaryVault);
            }
            return bundlePath;
        }

        /// <summary>Puts the user's own data and the portable vault into the archive.</summary>
        static void WriteEntries(ZipArchive archive, string temporaryVault)
        {
            {
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
        }

        /// <summary>
        /// Opens a bundle for reading, whichever way it was written: the encrypted form, or the
        /// plain zip earlier versions produced. A wrong passphrase on an encrypted bundle is
        /// reported as itself rather than as a corrupt file.
        /// </summary>
        static ZipArchive OpenBundle(string bundlePath, string passphrase)
        {
            byte[] raw = File.ReadAllBytes(bundlePath);
            if (raw.Length < EncryptedMagic.Length || !raw.AsSpan(0, EncryptedMagic.Length).SequenceEqual(EncryptedMagic))
            {
                Log("Importing a bundle written before the archive itself was encrypted.", LogLevel.Warning);
                return new ZipArchive(new MemoryStream(raw), ZipArchiveMode.Read);
            }

            int saltStart = EncryptedMagic.Length;
            byte[] salt = raw.AsSpan(saltStart, CredentialVault.SaltBytes).ToArray();
            byte[] payload = raw.AsSpan(saltStart + CredentialVault.SaltBytes).ToArray();

            byte[] plain;
            try
            {
                plain = CredentialVault.DecryptAesGcm(payload, CredentialVault.DeriveKey(passphrase, salt));
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                throw new InvalidOperationException("That passphrase does not open this bundle.");
            }
            return new ZipArchive(new MemoryStream(plain), ZipArchiveMode.Read);
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
            using (var archive = OpenBundle(bundlePath, passphrase))
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
