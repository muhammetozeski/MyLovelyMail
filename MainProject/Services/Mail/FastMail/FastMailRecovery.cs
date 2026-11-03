using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail.FastMail;

/// <summary>Produces an encrypted standalone recovery script before local Fast Mail deletion.</summary>
public static class FastMailRecovery
{
    public sealed record RecoveryAccount(string Id, string EmailAddress);
    public sealed record RecoveryScript(string Path, IReadOnlyList<string> Files);
    public static RecoveryAccount FindRecoveryAccount() => AccountStore.Accounts
        .Where(static a => a.Protocol != IncomingProtocol.FastMail && !a.TorOnly)
        .Where(a => !string.IsNullOrEmpty(CredentialVault.GetPassword(a.Id)))
        .OrderBy(static a => a.CreatedUtc).Select(static a => new RecoveryAccount(a.Id, a.EmailAddress)).FirstOrDefault()
        ?? throw new InvalidOperationException("An unlocked normal mail account password is required for recovery encryption.");

    public static byte[] DeriveRecoveryKey(string password)
    {
        byte[] key = Encoding.UTF8.GetBytes(password);
        for (int i = 0; i < 1500; i++)
        {
            byte[] next = SHA256.HashData(key);
            CryptographicOperations.ZeroMemory(key);
            key = next;
        }
        return key;
    }

    public static RecoveryScript CreateRecoveryScript(MailAccountData account, RecoveryAccount recoveryAccount)
    {
        if (account.Protocol != IncomingProtocol.FastMail || !Guid.TryParse(account.Id, out _))
            throw new InvalidOperationException("Only a valid Fast Mail account can be deleted this way.");
        if (AccountStore.GetById(recoveryAccount.Id) is not { TorOnly: false } normal || normal.Protocol == IncomingProtocol.FastMail)
            throw new InvalidOperationException("Recovery encryption requires a normal mail account.");
        string password = CredentialVault.GetPassword(recoveryAccount.Id) ?? throw new InvalidOperationException("Unlock the recovery account password first.");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in MessageStore.AccountRoots(account.Id))
        {
            if (!Directory.Exists(root)) continue;
            if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Account folders cannot be symbolic links.");
            foreach (string file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
            { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })) files.Add(file);
        }
        string stageRoot = Path.GetFullPath(Path.Combine(AppPaths.AppCache, "ComposeAttachments")) + Path.DirectorySeparatorChar;
        foreach (var folder in MessageStore.GetFolders(account.Id).Where(static f => f.IsLocal && f.Role == FolderRole.Drafts))
        foreach (var summary in MessageStore.GetSummaries(account.Id, folder.FullName))
        {
            using var draft = MessageStore.TryLoadMimeMessage(account.Id, folder.FullName, summary.Uid);
            foreach (string path in (draft?.Headers["X-LovelyDraft-Attachments"] ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                string fullPath = Path.GetFullPath(path);
                if (fullPath.StartsWith(stageRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath)
                    && !File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint)) files.Add(fullPath);
            }
        }
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var metadata = zip.CreateEntry("metadata.json").Open())
                JsonSerializer.Serialize(metadata, new { AccountId = account.Id, Password = CredentialVault.GetPassword(account.Id) ?? string.Empty });
            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(AppPaths.Root, file).Replace('\\', '/');
                if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) throw new IOException("A backup file escaped the application root.");
                zip.CreateEntryFromFile(file, "files/" + relative, CompressionLevel.Optimal);
            }
        }
        byte[] plain = buffer.ToArray();
        byte[] key = DeriveRecoveryKey(password);
        byte[] encrypted;
        try
        {
            encrypted = CredentialVault.EncryptAesGcm(plain, key);
            byte[] verified = CredentialVault.DecryptAesGcm(encrypted, key);
            try { if (!CryptographicOperations.FixedTimeEquals(plain, verified)) throw new CryptographicException("Recovery verification failed."); }
            finally { CryptographicOperations.ZeroMemory(verified); }
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
        using var template = typeof(FastMailRecovery).Assembly.GetManifestResourceStream("MyLovelyMail.MainProject.Services.Mail.FastMail.RestoreFastMail.ps1")
            ?? throw new InvalidOperationException("The recovery script template is missing.");
        using var reader = new StreamReader(template);
        string script = reader.ReadToEnd().Replace("__ENCRYPTED_PAYLOAD__", Convert.ToBase64String(encrypted))
            .Replace("__APP_ROOT__", AppPaths.Root.Replace("'", "''")).Replace("__RECOVERY_ACCOUNT__", recoveryAccount.EmailAddress.Replace("'", "''"));
        string destination = Path.Combine(AppPaths.UserData, "RecoveryStaging", $"RestoreFastMail-{account.Id}-{Guid.NewGuid():N}.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        AtomicFile.WriteAllText(destination, script);
        return new RecoveryScript(destination, files.ToArray());
    }

    public static async Task DeleteAsync(MailAccountData account, RecoveryAccount recoveryAccount, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Encrypted deletion to the Recycle Bin requires Windows.");
        if (account.Protocol != IncomingProtocol.FastMail || !Guid.TryParse(account.Id, out _)) throw new InvalidOperationException("This operation is only for valid Fast Mail accounts.");
        using var deletion = await FastMailAccountOperations.BeginDeleteAsync(account.Id, cancellationToken);
        try
        {
            string[] roots = MessageStore.AccountRoots(account.Id).Select(Path.GetFullPath).ToArray();
            string[] folders = MessageStore.GetFolders(account.Id).Select(static f => f.FullName).ToArray();
            var script = await Task.Run(() => CreateRecoveryScript(account, recoveryAccount), cancellationToken);
            FileSystem.DeleteFile(script.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            // Plaintext is removed only after the authenticated ciphertext script reached the Recycle Bin.
            foreach (string folder in folders) MessageStore.RemoveFolder(account.Id, folder);
            foreach (string root in roots)
            {
                string expected = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(root))!, "Accounts", account.Id);
                if (!string.Equals(root, Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe deletion root.");
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            foreach (string file in script.Files.Where(file => !roots.Any(root => file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
                if (File.Exists(file)) File.Delete(file);
            CredentialVault.RemovePassword(account.Id);
            AccountStore.Remove(account.Id);
            deletion.Complete();
        }
        catch
        {
            throw;
        }
    }
}
