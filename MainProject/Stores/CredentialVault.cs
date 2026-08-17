using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>
    /// Encrypted store of account passwords, keyed by account id. Two at-rest modes selected by
    /// <see cref="Settings.CredentialVaultMode"/>: DPAPI (bound to the Windows user — unlocks
    /// automatically, but a copied UserData folder cannot open it on another machine) and
    /// AES-GCM under a PBKDF2 master password (portable, asks the user to unlock). The portable
    /// export/import bundle always uses the passphrase path so migration works from either mode.
    /// Vault file: <c>UserData/vault.json</c>.
    /// </summary>
    public static class CredentialVault
    {
        public const string VaultFileName = "vault.json";
        const int Pbkdf2Iterations = 200_000;
        const int SaltBytes = 16;
        const int KeyBytes = 32;

        static string VaultPath => Path.Combine(AppPaths.UserData, VaultFileName);

        static Dictionary<string, string> secrets = [];
        static byte[]? masterKey;
        static byte[] masterSalt = [];

        /// <summary>True when secrets are readable (DPAPI mode after load, or master mode after a correct unlock).</summary>
        public static bool IsUnlocked { get; private set; }

        /// <summary>True when the vault file exists in master-password mode and has not been unlocked yet.</summary>
        public static bool NeedsMasterPassword { get; private set; }

        public static event Action? OnVaultStateChanged;

        sealed class VaultFile
        {
            public VaultMode Mode { get; set; }
            public string Salt { get; set; } = string.Empty;
            public string Payload { get; set; } = string.Empty;
        }

        #region Load / unlock

        /// <summary>Loads the vault at startup. DPAPI vaults unlock immediately; master-password vaults wait for <see cref="UnlockWithMasterPassword"/>.</summary>
        public static void Load()
        {
            secrets = [];
            masterKey = null;
            IsUnlocked = false;
            NeedsMasterPassword = false;

            if (!File.Exists(VaultPath))
            {
                // Fresh install: an empty vault in the configured mode is unlocked by definition
                // (nothing to decrypt). Master mode still needs a password before the FIRST save.
                IsUnlocked = Settings.CredentialVaultMode.Value == VaultMode.Dpapi;
                NeedsMasterPassword = !IsUnlocked;
                OnVaultStateChanged?.Invoke();
                return;
            }

            try
            {
                var file = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(VaultPath));
                if (file == null) return;

                masterSalt = string.IsNullOrEmpty(file.Salt) ? [] : Convert.FromBase64String(file.Salt);

                if (file.Mode == VaultMode.Dpapi)
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        Log("DPAPI vault found on a non-Windows platform; it cannot be opened here.", LogLevel.Error);
                        return;
                    }
                    byte[] plain = System.Security.Cryptography.ProtectedData.Unprotect(
                        Convert.FromBase64String(file.Payload), null, DataProtectionScope.CurrentUser);
                    secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
                    IsUnlocked = true;
                }
                else
                {
                    NeedsMasterPassword = true;
                }
            }
            catch (Exception ex)
            {
                Log($"Could not load credential vault: {ex}", LogLevel.Error);
            }
            OnVaultStateChanged?.Invoke();
        }

        /// <summary>Tries to open a master-password vault. Returns false on a wrong password.</summary>
        public static bool UnlockWithMasterPassword(string masterPassword)
        {
            try
            {
                var file = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(VaultPath));
                if (file == null || file.Mode != VaultMode.MasterPassword) return false;

                masterSalt = Convert.FromBase64String(file.Salt);
                byte[] key = DeriveKey(masterPassword, masterSalt);
                byte[] plain = DecryptAesGcm(Convert.FromBase64String(file.Payload), key);

                secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
                masterKey = key;
                IsUnlocked = true;
                NeedsMasterPassword = false;
                OnVaultStateChanged?.Invoke();
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Log($"Vault unlock failed: {ex}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Sets the master password for a fresh (or newly switched) master-mode vault and unlocks it.
        /// Existing secrets in memory are kept and re-encrypted under the new key.
        /// </summary>
        public static void SetMasterPassword(string masterPassword)
        {
            masterSalt = RandomNumberGenerator.GetBytes(SaltBytes);
            masterKey = DeriveKey(masterPassword, masterSalt);
            IsUnlocked = true;
            NeedsMasterPassword = false;
            Save();
            OnVaultStateChanged?.Invoke();
        }

        #endregion

        #region Secrets

        public static string? GetPassword(string accountId) =>
            IsUnlocked && secrets.TryGetValue(accountId, out var password) ? password : null;

        public static void SetPassword(string accountId, string password)
        {
            secrets[accountId] = password;
            Log($"Vault: password stored for account {accountId}.");
            Save();
        }

        public static void RemovePassword(string accountId)
        {
            if (secrets.Remove(accountId))
                Save();
        }

        #endregion

        #region Persistence

        static void Save()
        {
            if (!IsUnlocked)
            {
                Log("Vault save skipped: vault is locked.", LogLevel.Warning);
                return;
            }

            var mode = Settings.CredentialVaultMode.Value;
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(secrets);

            string payload;
            if (mode == VaultMode.Dpapi)
            {
                if (!OperatingSystem.IsWindows())
                {
                    Log("DPAPI vault mode is Windows-only; switch to the master password mode.", LogLevel.Error);
                    return;
                }
                payload = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
                    plain, null, DataProtectionScope.CurrentUser));
            }
            else
            {
                if (masterKey == null)
                {
                    Log("Vault save skipped: master mode has no key yet (set a master password first).", LogLevel.Warning);
                    return;
                }
                payload = Convert.ToBase64String(EncryptAesGcm(plain, masterKey));
            }

            var file = new VaultFile
            {
                Mode = mode,
                Salt = Convert.ToBase64String(masterSalt),
                Payload = payload
            };
            AtomicFile.WriteAllText(VaultPath, JsonSerializer.Serialize(file));
        }

        /// <summary>Re-encrypts the vault in the newly selected mode (call after changing <see cref="Settings.CredentialVaultMode"/>).</summary>
        public static void ReencryptInCurrentMode() => Save();

        /// <summary>True when a vault file exists on disk (distinguishes "set a NEW master password" from "unlock").</summary>
        public static bool VaultFileExists => File.Exists(VaultPath);

        /// <summary>Switches an unlocked vault to master-password protection under the given password.</summary>
        public static void SwitchToMasterPassword(string newMasterPassword)
        {
            Settings.CredentialVaultMode.Set(VaultMode.MasterPassword);
            SettingsManager.SaveSettings();
            SetMasterPassword(newMasterPassword);
        }

        /// <summary>Switches an unlocked vault back to DPAPI. False when locked or not on Windows.</summary>
        public static bool SwitchToDpapi()
        {
            if (!IsUnlocked || !OperatingSystem.IsWindows()) return false;
            Settings.CredentialVaultMode.Set(VaultMode.Dpapi);
            SettingsManager.SaveSettings();
            masterKey = null;
            Save();
            OnVaultStateChanged?.Invoke();
            return true;
        }

        /// <summary>Drops the in-memory key and secrets; the unlock screen takes over (master mode only).</summary>
        public static void LockNow()
        {
            if (Settings.CredentialVaultMode.Value != VaultMode.MasterPassword) return;
            secrets = [];
            masterKey = null;
            IsUnlocked = false;
            NeedsMasterPassword = true;
            OnVaultStateChanged?.Invoke();
        }

        #endregion

        #region Portable migration bundle

        /// <summary>Writes every secret encrypted under the given passphrase — machine-independent, for the migration bundle.</summary>
        public static void ExportPortable(string path, string passphrase)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
            byte[] key = DeriveKey(passphrase, salt);
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(secrets);
            var file = new VaultFile
            {
                Mode = VaultMode.MasterPassword,
                Salt = Convert.ToBase64String(salt),
                Payload = Convert.ToBase64String(EncryptAesGcm(plain, key))
            };
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file));
        }

        /// <summary>Merges secrets from a portable export into the live vault. Returns false on a wrong passphrase.</summary>
        public static bool ImportPortable(string path, string passphrase)
        {
            try
            {
                var file = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(path));
                if (file == null) return false;
                byte[] key = DeriveKey(passphrase, Convert.FromBase64String(file.Salt));
                byte[] plain = DecryptAesGcm(Convert.FromBase64String(file.Payload), key);
                var imported = JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
                foreach (var (accountId, password) in imported)
                    secrets[accountId] = password;
                Save();
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Log($"Portable vault import failed: {ex}", LogLevel.Error);
                return false;
            }
        }

        #endregion

        #region Crypto helpers

        static byte[] DeriveKey(string password, byte[] salt) =>
            Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyBytes);

        /// <summary>Output layout: nonce (12) + tag (16) + ciphertext.</summary>
        static byte[] EncryptAesGcm(byte[] plain, byte[] key)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
            byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize];
            byte[] cipher = new byte[plain.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plain, cipher, tag);
            return [.. nonce, .. tag, .. cipher];
        }

        static byte[] DecryptAesGcm(byte[] blob, byte[] key)
        {
            int nonceLength = AesGcm.NonceByteSizes.MaxSize;
            int tagLength = AesGcm.TagByteSizes.MaxSize;
            byte[] nonce = blob[..nonceLength];
            byte[] tag = blob[nonceLength..(nonceLength + tagLength)];
            byte[] cipher = blob[(nonceLength + tagLength)..];
            byte[] plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, tagLength);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }

        #endregion
    }
}
