using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>
    /// The vault's device-bound protection on Android: AES-256-GCM under a key generated inside the
    /// Android Keystore, which never hands the key material out. The app opens the vault at every
    /// start without a password — that is what lets it sync right after the phone boots — and the
    /// vault file copied off the phone opens nowhere, because the key stays behind.
    /// </summary>
    public static class DeviceProtection
    {
        const string KeyStoreProvider = "AndroidKeyStore";
        const string KeyAlias = "MyLovelyMail.CredentialVault";
        const string Transformation = "AES/GCM/NoPadding";
        const int KeySizeBits = 256;
        const int IvBytes = 12;
        const int TagBits = 128;

        /// <summary>The vault mode's name on the settings page.</summary>
        public const string DisplayName = "This phone (Android Keystore)";

        /// <summary>The line under the name: what the mode gives and what it costs.</summary>
        public const string Description = "Unlocks automatically on this phone; the key never leaves it, so the vault cannot be opened anywhere else.";

        /// <summary>Nothing beside the vault belongs to the phone: the key lives in the Keystore, not in a file.</summary>
        public static readonly string[] MachineBoundFileNames = [];

        /// <summary>Every supported Android version has the Keystore.</summary>
        public static bool IsAvailable => true;

        /// <summary>Encrypts the vault payload under the Keystore key.</summary>
        /// <param name="plain">The serialized secrets.</param>
        /// <returns>The 12-byte IV followed by the ciphertext and its 16-byte tag.</returns>
        public static byte[] Protect(byte[] plain)
        {
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.EncryptMode, VaultKey());
            // The Keystore picks the IV itself and refuses a caller-supplied one for encryption.
            byte[] iv = cipher.GetIV()!;
            return [.. iv, .. cipher.DoFinal(plain)!];
        }

        /// <summary>Decrypts what <see cref="Protect"/> wrote; a changed byte fails the GCM tag check.</summary>
        /// <param name="payload">The IV followed by the ciphertext and tag.</param>
        /// <returns>The serialized secrets.</returns>
        public static byte[] Unprotect(byte[] payload)
        {
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, VaultKey(), new GCMParameterSpec(TagBits, payload, 0, IvBytes));
            return cipher.DoFinal(payload, IvBytes, payload.Length - IvBytes)!;
        }

        /// <summary>
        /// The vault key, generated on first use. Android keeps it until the app is uninstalled or its
        /// data is cleared, which are the same two moments the vault file itself disappears.
        /// </summary>
        static IKey VaultKey()
        {
            var keyStore = KeyStore.GetInstance(KeyStoreProvider)!;
            keyStore.Load(null);
            if (keyStore.GetKey(KeyAlias, null) is { } existing) return existing;

            var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreProvider)!;
            generator.Init(new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                .SetKeySize(KeySizeBits)
                .Build());
            Log("Created the credential vault key in the Android Keystore.");
            return generator.GenerateKey()!;
        }
    }
}
