using System.Net.Sockets;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Security;
using MyLovelyMail.MainProject.DataModels.Mail;

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

        /// <summary>The pairs worth trying per protocol, most likely first. AUTH is never attempted.</summary>
        static readonly Dictionary<MailProtocol, (int Port, ConnectionSecurity Security)[]> Candidates = new()
        {
            [MailProtocol.Imap] = [(993, ConnectionSecurity.SslOnConnect), (143, ConnectionSecurity.StartTls), (143, ConnectionSecurity.None)],
            [MailProtocol.Pop3] = [(995, ConnectionSecurity.SslOnConnect), (110, ConnectionSecurity.StartTls)],
            [MailProtocol.Smtp] = [(465, ConnectionSecurity.SslOnConnect), (587, ConnectionSecurity.StartTls), (25, ConnectionSecurity.StartTls)]
        };

        /// <summary>Which server is being talked to; also the word that goes in the sentence.</summary>
        public enum MailProtocol { Imap, Pop3, Smtp }

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
        /// Connects (and nothing more) to each candidate pair until one answers. Only worth calling
        /// for a socket or TLS failure: an auth rejection means the settings already work.
        /// </summary>
        public static async Task<ConnectDiagnosis> ProbeAsync(ConnectDiagnosis diagnosis, MailProtocol protocol, string host,
            CancellationToken cancellationToken = default)
        {
            if (diagnosis.Kind is DiagnosisKind.AuthenticationRejected or DiagnosisKind.HostNotFound or DiagnosisKind.Ok)
                return diagnosis;

            foreach (var (port, security) in Candidates[protocol])
            {
                if (!await AnswersAsync(protocol, host, port, security, cancellationToken)) continue;

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
            CancellationToken cancellationToken)
        {
            var options = security switch
            {
                ConnectionSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
                ConnectionSecurity.StartTls => SecureSocketOptions.StartTls,
                ConnectionSecurity.None => SecureSocketOptions.None,
                _ => SecureSocketOptions.Auto
            };

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(ProbeTimeout);
            try
            {
                switch (protocol)
                {
                    case MailProtocol.Imap:
                    {
                        using var client = new ImapClient();
                        await client.ConnectAsync(host, port, options, budget.Token);
                        await client.DisconnectAsync(true, budget.Token);
                        return true;
                    }
                    case MailProtocol.Pop3:
                    {
                        using var client = new Pop3Client();
                        await client.ConnectAsync(host, port, options, budget.Token);
                        await client.DisconnectAsync(true, budget.Token);
                        return true;
                    }
                    default:
                    {
                        using var client = new SmtpClient();
                        await client.ConnectAsync(host, port, options, budget.Token);
                        await client.DisconnectAsync(true, budget.Token);
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
    }
}
