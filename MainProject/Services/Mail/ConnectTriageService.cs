using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Security;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services.Tor;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>What kind of thing went wrong, which decides what is worth suggesting.</summary>
    public enum DiagnosisKind
    {
        Ok,
        HostNotFound,
        ConnectionRefused,
        TlsHandshake,
        AuthenticationRejected,
        Timeout,
        Other
    }

    /// <summary>A named failure and, where one exists, the port/security pair that answered instead.</summary>
    public sealed record ConnectDiagnosis(
        DiagnosisKind Kind,
        string Sentence,
        string Stage,
        int? SuggestedPort = null,
        ConnectionSecurity? SuggestedSecurity = null);

    /// <summary>
    /// Turns a failed connection into a sentence that names WHAT broke and WHERE, and — when the
    /// settings are the plausible cause — finds the port and security that the same host does
    /// answer on.
    /// <para>
    /// The wizard printed raw MailKit text and never said whether the incoming or the sending
    /// server refused, and a wrong port could only be fixed by deleting the account and starting
    /// over. The Custom preset is a blank host with a guessed 993/995/465, so every self-hosted or
    /// corporate server lands there — and the port/security pairing is exactly what a preset
    /// cannot know.
    /// </para>
    /// </summary>
    public static class ConnectTriageService
    {
        /// <summary>Connect-only probe budget. Short on purpose: this runs several times in a row while someone waits.</summary>
        static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

        /// <summary>The same probe with a circuit to build first, which six seconds does not cover.</summary>
        static readonly TimeSpan TorProbeTimeout = TimeSpan.FromSeconds(45);

        /// <summary>The pairs worth trying per protocol, most likely first. AUTH is never attempted.</summary>
        static readonly Dictionary<MailProtocol, (int Port, ConnectionSecurity Security)[]> Candidates = new()
        {
            [MailProtocol.Imap] = [(993, ConnectionSecurity.SslOnConnect), (143, ConnectionSecurity.StartTls), (143, ConnectionSecurity.None)],
            [MailProtocol.Pop3] = [(995, ConnectionSecurity.SslOnConnect), (110, ConnectionSecurity.StartTls)],
            [MailProtocol.Smtp] = [(465, ConnectionSecurity.SslOnConnect), (587, ConnectionSecurity.StartTls), (25, ConnectionSecurity.StartTls)]
        };

        /// <summary>Which server is being talked to; also the word that goes in the sentence.</summary>
        public enum MailProtocol { Imap, Pop3, Smtp }

        /// <summary>
        /// The candidates worth offering THIS account. A Tor-only account never gets the
        /// unencrypted pair suggested: the connection path refuses it anyway, so a suggestion the
        /// user could click — "It does answer on port 143 with None" — would be an invitation to
        /// hand the login to an exit relay in the clear, printed by the diagnostic that is supposed
        /// to be protecting them. An onion host is exempt for the same reason it is exempt there.
        /// </summary>
        static IEnumerable<(int Port, ConnectionSecurity Security)> CandidatesFor(MailProtocol protocol, MailAccountData? account, string host) =>
            account is { TorOnly: true } && !MailConnections.IsOnionHost(host)
                ? Candidates[protocol].Where(static c => c.Security != ConnectionSecurity.None)
                : Candidates[protocol];

        /// <summary>Names the failure. <paramref name="stage"/> is what the user reads: "incoming server", "sending server".</summary>
        public static ConnectDiagnosis Classify(Exception exception, string stage) => exception switch
        {
            AuthenticationException or MailKit.ServiceNotAuthenticatedException =>
                new ConnectDiagnosis(DiagnosisKind.AuthenticationRejected,
                    $"The {stage} rejected the username or password.", stage),

            SslHandshakeException =>
                new ConnectDiagnosis(DiagnosisKind.TlsHandshake,
                    $"The {stage} answered, but the secure handshake failed — the security setting is probably wrong for this port.", stage),

            SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain } =>
                new ConnectDiagnosis(DiagnosisKind.HostNotFound,
                    $"The {stage} address could not be found — check the host name.", stage),

            SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
                new ConnectDiagnosis(DiagnosisKind.ConnectionRefused,
                    $"The {stage} refused the connection on that port.", stage),

            OperationCanceledException or TimeoutException =>
                new ConnectDiagnosis(DiagnosisKind.Timeout,
                    $"The {stage} did not answer in time — the port may be blocked.", stage),

            _ => new ConnectDiagnosis(DiagnosisKind.Other, $"The {stage} failed: {exception.Message}", stage)
        };

        /// <summary>
        /// Which circuit a probe rides. The literal "probe" put every account's probes on ONE
        /// circuit: on a tor daemon or a Tor Browser — neither started with the IsolateDestAddr
        /// flag the app's own tor gets — that lets a single exit see two Tor-only mailboxes'
        /// servers from the same client. The account is the right key; a probe with no account
        /// falls back to the host, which at least keeps unrelated servers apart.
        /// </summary>
        static string IsolationKeyFor(MailAccountData? account, string host) =>
            account is { Id.Length: > 0 } ? account.Id : $"probe-{host}";

        /// <summary>
        /// Connects (and nothing more) to each candidate pair until one answers. Only worth calling
        /// for a socket or TLS failure: an auth rejection means the settings already work.
        /// <para>
        /// <paramref name="account"/> decides how the probe travels. For a Tor-only account every
        /// probe goes through Tor, and if no Tor route exists the probe is skipped entirely —
        /// a diagnostic that opened three direct sockets to the mail server would give away exactly
        /// what the flag exists to hide, and it would do it while telling the user about privacy.
        /// </para>
        /// </summary>
        public static async Task<ConnectDiagnosis> ProbeAsync(ConnectDiagnosis diagnosis, MailProtocol protocol, string host,
            CancellationToken cancellationToken = default, MailAccountData? account = null)
        {
            if (diagnosis.Kind is DiagnosisKind.AuthenticationRejected or DiagnosisKind.HostNotFound or DiagnosisKind.Ok)
                return diagnosis;

            TorEndpoint? torEndpoint = null;
            if (account is { TorOnly: true })
            {
                try
                {
                    torEndpoint = await TorService.RequireEndpointAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    Log($"Connect probe skipped: '{account.EmailAddress}' is Tor-only and no Tor route is available ({ex.Message}).", LogLevel.Warning);
                    return diagnosis with { Sentence = $"{diagnosis.Sentence} Ports could not be probed: this account may only be reached through Tor, and Tor is not available." };
                }
            }

            foreach (var (port, security) in CandidatesFor(protocol, account, host))
            {
                if (!await AnswersAsync(protocol, host, port, security, torEndpoint, IsolationKeyFor(account, host), cancellationToken)) continue;

                Log($"Connect probe: {host} answers on {port} ({security}).");
                return diagnosis with
                {
                    Sentence = $"{diagnosis.Sentence} It does answer on port {port} with {security}.",
                    SuggestedPort = port,
                    SuggestedSecurity = security
                };
            }
            return diagnosis;
        }

        static async Task<bool> AnswersAsync(MailProtocol protocol, string host, int port, ConnectionSecurity security,
            TorEndpoint? torEndpoint, string isolationKey, CancellationToken cancellationToken)
        {
            var options = security switch
            {
                ConnectionSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
                ConnectionSecurity.StartTls => SecureSocketOptions.StartTls,
                ConnectionSecurity.None => SecureSocketOptions.None,
                _ => SecureSocketOptions.Auto
            };

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // A probe that has to build a circuit first cannot be held to a clear-net stopwatch.
            budget.CancelAfter(torEndpoint == null ? ProbeTimeout : TorProbeTimeout);
            try
            {
                switch (protocol)
                {
                    case MailProtocol.Imap:
                    {
                        using var client = new ImapClient();
                        await ProbeWithAsync(client, host, port, options, torEndpoint, isolationKey, budget.Token);
                        return true;
                    }
                    case MailProtocol.Pop3:
                    {
                        using var client = new Pop3Client();
                        await ProbeWithAsync(client, host, port, options, torEndpoint, isolationKey, budget.Token);
                        return true;
                    }
                    default:
                    {
                        using var client = new SmtpClient();
                        await ProbeWithAsync(client, host, port, options, torEndpoint, isolationKey, budget.Token);
                        return true;
                    }
                }
            }
            catch
            {
                // A candidate that does not answer is the normal case, not a fault worth logging.
                return false;
            }
        }

        /// <summary>Connect, disconnect — through Tor when an endpoint was handed in, and never any other way.</summary>
        static async Task ProbeWithAsync(MailService client, string host, int port, SecureSocketOptions options,
            TorEndpoint? torEndpoint, string isolationKey, CancellationToken cancellationToken)
        {
            if (torEndpoint != null)
            {
                client.ProxyClient = TorService.CreateProxy(torEndpoint, isolationKey, useOwnClient: false);
                // Same reason as the connection path: online revocation is fetched by the OS,
                // outside the proxy, so leaving it on would send the server's certificate serial
                // to the CA in the clear from a probe run on the account's behalf.
                client.CheckCertificateRevocation = false;
            }

            await client.ConnectAsync(host, port, options, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
    }
}
