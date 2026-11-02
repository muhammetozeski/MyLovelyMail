using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;

namespace MyLovelyMail.MainProject.Services.Mail.FastMail;

public sealed record FastMailProvisionRequest(
    string IsolationKey,
    string? DesiredUsername,
    string? DesiredDomain,
    string? Password,
    bool UseRandomUsername);

public sealed record FastMailProvisionResult(
    string EmailAddress,
    string Username,
    string Domain,
    string RemoteAccountId,
    string PasswordOrSecret);

public sealed record FastMailMessageItem(
    string RemoteId,
    string MessageIdHeader,
    string FromName,
    string FromAddress,
    string ToAddresses,
    string Subject,
    string PreviewText,
    DateTime DateUtc,
    bool IsSeen,
    bool HasAttachments,
    long SizeBytes);

/// <summary>
/// Contract implemented by every Fast Mail ("Hızlı Mail") provider. Implementing this single
/// interface and registering the instance in <see cref="FastMailProviderRegistry"/> makes the
/// provider available in the Fast Mail creation modal, background sync, reader, and message actions.
/// <para>
/// Every implementation MUST route all network traffic exclusively through
/// <see cref="Tor.TorService.CreateIsolatedHttpClient"/> so no Fast Mail communication ever leaves
/// the machine outside the account's isolated Tor circuit.
/// </para>
/// </summary>
public interface IFastMailProvider
{
    string Id { get; }
    string DisplayName { get; }
    string Emoji { get; }
    string Description { get; }
    string ApiHost { get; }

    bool SupportsCustomUsername { get; }
    bool SupportsDomainSelection { get; }
    bool SupportsPassword { get; }

    /// <summary>Returns the selectable email domains for this provider (fetched over Tor or known active domains).</summary>
    Task<IReadOnlyList<string>> GetDomainsAsync(string isolationKey, CancellationToken cancellationToken);

    /// <summary>Creates or claims the mailbox on the provider over Tor and returns its address and credentials.</summary>
    Task<FastMailProvisionResult> ProvisionAccountAsync(FastMailProvisionRequest request, CancellationToken cancellationToken);

    /// <summary>Lists messages currently in the provider's inbox over Tor.</summary>
    Task<IReadOnlyList<FastMailMessageItem>> ListMessagesAsync(MailAccountData account, CancellationToken cancellationToken);

    /// <summary>Downloads the full message (raw MIME or synthesized <see cref="MimeMessage"/>) over Tor.</summary>
    Task<MimeMessage> DownloadMessageAsync(MailAccountData account, string remoteId, CancellationToken cancellationToken);

    /// <summary>Updates the seen/read state on the remote provider when supported.</summary>
    Task MarkReadAsync(MailAccountData account, IReadOnlyList<string> remoteIds, bool seen, CancellationToken cancellationToken);

    /// <summary>Deletes messages on the remote provider when supported.</summary>
    Task DeleteMessagesAsync(MailAccountData account, IReadOnlyList<string> remoteIds, CancellationToken cancellationToken);
}

public static class FastMailProviderRegistry
{
    static readonly List<IFastMailProvider> providers =
    [
        new GuerrillaMailProvider(),
        new MailTmProvider()
    ];

    public static IReadOnlyList<IFastMailProvider> All => providers;

    public static IFastMailProvider? GetById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return providers.FirstOrDefault(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static void Register(IFastMailProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        int existing = providers.FindIndex(p => string.Equals(p.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) providers[existing] = provider;
        else providers.Add(provider);
    }
}
