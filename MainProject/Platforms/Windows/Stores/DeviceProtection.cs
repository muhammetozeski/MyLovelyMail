using System.Runtime.Versioning;
using System.Security.Cryptography;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>
    /// The vault's device-bound protection on Windows: DPAPI under the signed-in Windows user, mixed
    /// with an entropy file kept beside the vault. The same user on the same machine opens the vault
    /// without a password; the vault copied anywhere else opens nowhere.
    /// </summary>
    public static class DeviceProtection
    {
        const string EntropyFileName = "vault.entropy";
        const int EntropyBytes = 32;

        /// <summary>The vault mode's name on the settings page.</summary>
        public const string DisplayName = "Windows account (DPAPI)";

        /// <summary>The line under the name: what the mode gives and what it costs.</summary>
        public const string Description = "Unlocks automatically on this Windows account; not portable to other machines.";

        /// <summary>Files beside the vault that belong to this machine; a migration bundle leaves them behind.</summary>
        public static readonly string[] MachineBoundFileNames = [EntropyFileName];

        static string EntropyPath => Path.Combine(AppPaths.UserData, EntropyFileName);

        /// <summary>DPAPI exists only on Windows; the web preview running elsewhere has no device protection.</summary>
        [SupportedOSPlatformGuard("windows")]
        public static bool IsAvailable => OperatingSystem.IsWindows();

        /// <summary>Encrypts the vault payload for the current Windows user.</summary>
        /// <param name="plain">The serialized secrets.</param>
        /// <returns>The DPAPI blob.</returns>
        [SupportedOSPlatform("windows")]
        public static byte[] Protect(byte[] plain) =>
            ProtectedData.Protect(plain, Entropy(), DataProtectionScope.CurrentUser);

        /// <summary>
        /// Opens a DPAPI payload written either way. A vault from before the entropy existed was
        /// protected with none, and it must keep opening — the alternative is a user whose saved
        /// passwords vanish on upgrade. The next save re-protects it with entropy, so the fallback
        /// is used once per vault and then never again.
        /// </summary>
        /// <param name="payload">The DPAPI blob read from the vault file.</param>
        /// <returns>The serialized secrets.</returns>
        [SupportedOSPlatform("windows")]
        public static byte[] Unprotect(byte[] payload)
        {
            try
            {
                return ProtectedData.Unprotect(payload, Entropy(), DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                byte[] plain = ProtectedData.Unprotect(payload, null, DataProtectionScope.CurrentUser);
                Log("Opened a vault written before the DPAPI entropy; the next save re-protects it with one.");
                return plain;
            }
        }

        /// <summary>
        /// Extra input mixed into the DPAPI protection, created once and kept beside the vault.
        /// <para>
        /// Without it — and it was null — ANY process running as this Windows user could read
        /// vault.json, base64-decode the payload, call Unprotect with the same null, and get every
        /// mail password in the clear. That is the price of "unlocks automatically", but it does
        /// not have to be that cheap: with entropy the attacker needs to have read a second file
        /// as well, which is the difference between "any code as this user" and "any code as this
        /// user that also went looking".
        /// </para>
        /// <para>
        /// Machine-bound like the vault itself, so it does NOT travel in the migration bundle:
        /// importing it would replace the entropy on the target machine and leave whatever vault
        /// that machine already had protected by a value no longer on disk.
        /// </para>
        /// </summary>
        static byte[] Entropy()
        {
            try
            {
                if (File.Exists(EntropyPath)) return File.ReadAllBytes(EntropyPath);

                byte[] fresh = RandomNumberGenerator.GetBytes(EntropyBytes);
                Directory.CreateDirectory(AppPaths.UserData);
                File.WriteAllBytes(EntropyPath, fresh);
                Log("Created the vault's DPAPI entropy file.");
                return fresh;
            }
            catch (Exception ex)
            {
                // Never fail the vault over this: an unreadable entropy file must leave the user
                // with a working mailbox, not a locked one. Empty entropy is what the old vaults
                // were protected with anyway.
                Log($"Could not read or create the vault entropy, falling back to none: {ex.Message}", LogLevel.Warning);
                return [];
            }
        }
    }
}
