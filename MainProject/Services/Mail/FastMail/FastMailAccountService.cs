using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail.FastMail;

/// <summary>Provisions and stores a Fast Mail account for both the wizard and one-click creation.</summary>
public static class FastMailAccountService
{
    public static Task<MailAccountData> CreateMailTmAsync(string folderId, CancellationToken cancellationToken = default) =>
        CreateAsync(new MailTmProvider(), new FastMailProvisionRequest(Guid.NewGuid().ToString("N"), null, null, null, true),
            folderId, cancellationToken: cancellationToken);

    public static async Task<MailAccountData> CreateAsync(IFastMailProvider provider, FastMailProvisionRequest request,
        string? folderId = null, string title = "", string note = "", string displayName = "", string? colorHex = null,
        CancellationToken cancellationToken = default)
    {
        if (!CredentialVault.IsUnlocked) throw new InvalidOperationException("Unlock the credential vault first.");
        var provision = await provider.ProvisionAccountAsync(request, cancellationToken);
        var account = new MailAccountData
        {
            Id = request.IsolationKey, DisplayName = displayName.Trim(), EmailAddress = provision.EmailAddress,
            FolderId = AccountFolderStore.NormalizeFolderId(folderId), Title = title.Trim(), Note = note.Trim(),
            Protocol = IncomingProtocol.FastMail, FastMailProviderId = provider.Id,
            FastMailRemoteAccountId = provision.RemoteAccountId, IncomingHost = provider.ApiHost, IncomingPort = 443,
            IncomingSecurity = ConnectionSecurity.SslOnConnect, IncomingUsername = provision.Username,
            SmtpHost = string.Empty, SmtpPort = 25, SmtpSecurity = ConnectionSecurity.StartTls, TorOnly = true
        };
        if (!string.IsNullOrWhiteSpace(colorHex)) account.ColorHex = colorHex;
        CredentialVault.SetPassword(account.Id, provision.PasswordOrSecret);
        AccountStore.Save(account);
        MessageStore.SaveFolder(new MailFolderData
        {
            AccountId = account.Id, FullName = MessageStore.InboxFullName, DisplayName = "Inbox", Role = FolderRole.Inbox
        });
        return account;
    }
}
