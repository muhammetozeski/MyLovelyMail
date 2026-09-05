using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Security;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services.Tor;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Builds connected + authenticated MailKit clients from a <see cref="MailAccountData"/>,
    /// pulling the password from <see cref="CredentialVault"/>. One place maps
    /// <see cref="ConnectionSecurity"/> to MailKit's <see cref="SecureSocketOptions"/>.
    /// <para>
    /// This is also the only door to a mail server in the app, which is what makes
    /// <see cref="MailAccountData.TorOnly"/> enforceable rather than aspirational: an account with
    /// that flag gets a Tor proxy attached to its client here, or no connection at all. There is no
    /// branch below that opens a direct socket for such an account, and
    /// <see cref="RequireTunnel"/> re-checks that at the last moment before every connect.
    /// </para>
    /// </summary>
    public static class MailConnections
    {
        /// <summary>How long MailKit waits on one Tor round-trip. Three relays are not one hop, and its 2-minute default is measured for one.</summary>
        const int TorClientTimeoutMs = 240_000;

        /// <summary>
        /// What ONE rung may spend before the ladder moves on.
        /// <para>
        /// Without it a rung could spend the whole attempt: Tor's own SocksTimeout default is two
        /// minutes, so a proxy that accepted the connection and then could not attach the stream
        /// held rung 1 for that long, rung 2 got the remainder, and Polly restarted the whole
        /// ladder at rung 1 on the retry. Six passes, twelve minutes, and "the app's own SOCKS5
        /// client", "a rediscovered endpoint" and "a tor started by this app" were never reached —
        /// the ladder's whole point is that each rung changes one thing, and that only holds for
        /// rungs that fail fast.
        /// </para>
        /// <para>
        /// It guards the CONNECT, not the endpoint work before it: starting a tor is bounded by
        /// its own bootstrap timeout and can legitimately take minutes on a first run.
        /// </para>
        /// </summary>
        static readonly TimeSpan TorRungBudget = TimeSpan.FromSeconds(45);

        /// <summary>
        /// Why a Tor-only client turns MailKit's revocation check OFF, which looks like the wrong
        /// direction and is not.
        /// <para>
        /// MailKit defaults <c>CheckCertificateRevocation</c> to true (verified: true on all three
        /// clients). With it on, SslStream validates the chain with ONLINE revocation, which on
        /// Windows is CryptoAPI fetching the certificate's OCSP or CRL URL over WinHTTP — a path
        /// that knows nothing about the SOCKS proxy. So the stream is tunnelled and then the
        /// machine posts the mail server's certificate serial to the CA in the clear, resolving
        /// the responder's name through the system resolver: the provider identity leaves anyway,
        /// which is the one thing the DOMAINNAME care in TorSocks5 exists to prevent.
        /// </para>
        /// <para>
        /// The other half is worse. On the usual Tor-only setup, where non-Tor traffic is blocked
        /// at the firewall, that fetch cannot complete, the chain builds RevocationStatusUnknown,
        /// MailKit refuses it, and every rung of the ladder ends in a TLS failure — the account
        /// never connects at all. The chain, the host name and the expiry are still verified; only
        /// the part of validation that deliberately leaves the tunnel is dropped.
        /// </para>
        /// </summary>
        const bool TorRevocationCheck = false;

        static SecureSocketOptions ToSocketOptions(ConnectionSecurity security) => security switch
        {
            ConnectionSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            ConnectionSecurity.StartTls => SecureSocketOptions.StartTls,
            ConnectionSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto
        };

        /// <summary>Ports that are TLS from the first byte. There is no STARTTLS to insist on here, and Auto already picks SslOnConnect.</summary>
        static readonly int[] ImplicitTlsPorts = [993, 995, 465];

        /// <summary>An onion address, where the rendezvous circuit is already encrypted end to end.</summary>
        public static bool IsOnionHost(string host) => host.TrimEnd('.').EndsWith(".onion", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The same mapping, hardened for an account that may only travel through Tor.
        /// <para>
        /// <see cref="SecureSocketOptions.Auto"/> means "SslOnConnect on 993/995/465, otherwise
        /// STARTTLS <em>if the server offers it</em>", and that last clause is the hole: a Tor exit
        /// relay reads and writes the plaintext side of the tunnel, so it can simply delete STARTTLS
        /// from the greeting and MailKit will carry on unencrypted. Measured against a listener that
        /// advertises no STARTTLS: with Auto the client sent <c>AUTHENTICATE PLAIN</c> and then the
        /// credentials, which arrived readable —
        /// <c>\0gizli-kullanici\0GIZLI-PAROLA-123</c>. With StartTls it sent nothing at all and
        /// failed with "does not support the STARTTLS extension", which is the right answer.
        /// </para>
        /// <para>
        /// So Auto becomes StartTls (the strict one) off the implicit-TLS ports, and None is refused
        /// outright — before any circuit is built, since no route can make a cleartext login safe.
        /// Onion hosts keep the plain mapping: the circuit itself is end-to-end encrypted and
        /// authenticated to the service key, and onion mail services commonly listen on plain 143.
        /// </para>
        /// </summary>
        internal static SecureSocketOptions ToSocketOptions(ConnectionSecurity security, MailAccountData account, string host, int port)
        {
            if (!account.TorOnly || IsOnionHost(host)) return ToSocketOptions(security);

            if (security == ConnectionSecurity.None)
                throw new TorUnavailableException(
                    $"'{account.EmailAddress}' may only be reached through Tor, and {host}:{port} is set to no encryption. "
                    + "The exit relay would read the password in the clear. Set the security to SSL or STARTTLS.",
                    recoverable: false);

            if (security == ConnectionSecurity.Auto)
                return ImplicitTlsPorts.Contains(port) ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

            return ToSocketOptions(security);
        }

        /// <summary>The vault password for the account, or an explanatory exception when locked/missing.</summary>
        static string RequirePassword(MailAccountData account, string? passwordOverride)
        {
            string? password = passwordOverride ?? CredentialVault.GetPassword(account.Id);
            if (password == null)
                throw new InvalidOperationException(CredentialVault.IsUnlocked
                    ? $"No password stored for account '{account.EmailAddress}'."
                    : "The credential vault is locked. Unlock it first.");
            return password;
        }

        /// <summary>
        /// Connects and authenticates one MailKit client. On any failure the half-open client is
        /// disposed before the exception continues, so no caller ever receives or leaks a broken
        /// connection.
        /// <para>
        /// Which of the two steps broke is recorded in the log and NOWHERE else. Wrapping the
        /// socket stage in an exception of this file's own was tried and had to come back out: two
        /// callers dispatch on the concrete type — <see cref="ConnectTriageService.Classify"/> maps
        /// SocketException and SslHandshakeException onto the sentence the wizard prints, and
        /// <see cref="ResiliencePolicy"/> reads OperationCanceledException as "do not retry" — so a
        /// wrapper turns a named diagnosis into "failed" and makes Polly retry an attempt that had
        /// already timed out. The ladder does not need the distinction either: its
        /// <c>catch (AuthenticationException)</c> sits after the connect and can only be reached by
        /// the login stage.
        /// </para>
        /// </summary>
        static async Task<TClient> ConnectAndAuthenticateAsync<TClient>(TClient client, string protocolName, string host, int port,
            SecureSocketOptions socketOptions, string username, string password, CancellationToken cancellationToken)
            where TClient : MailService
        {
            try
            {
                try
                {
                    await client.ConnectAsync(host, port, socketOptions, cancellationToken);
                }
                catch (Exception ex)
                {
                    Log($"{protocolName} could not reach {host}:{port} — {ex.GetType().Name}: {ex.Message}", LogLevel.Warning);
                    throw;
                }

                await client.AuthenticateAsync(username, password, cancellationToken);
                Log($"{protocolName} connected: {host}:{port}");
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Last line of defence for a Tor-only account: a client about to connect without a proxy
        /// attached would open a direct socket to the mail server. Nothing in this file can reach
        /// that state today; this exists so nothing added later can either.
        /// </summary>
        static void RequireTunnel(MailService client, MailAccountData account)
        {
            if (!account.TorOnly || client.ProxyClient != null) return;

            throw new TorUnavailableException(
                $"Refusing to connect: '{account.EmailAddress}' is a Tor-only account and no proxy was attached to the connection.",
                recoverable: false);
        }

        /// <summary>One rung of the Tor ladder — a different way to reach the same mailbox through Tor.</summary>
        /// <param name="Description">What is being tried, for the log line the user reads afterwards.</param>
        /// <param name="Rediscover">Re-run endpoint discovery instead of reusing the known SOCKS port.</param>
        /// <param name="StartOwnTor">Skip discovery and start a tor belonging to this app.</param>
        /// <param name="OwnSocksClient">Use the app's SOCKS5 client instead of MailKit's.</param>
        /// <param name="FreshCircuit">Move this account onto a new Tor circuit first.</param>
        sealed record TorRung(string Description, bool Rediscover, bool StartOwnTor, bool OwnSocksClient, bool FreshCircuit);

        /// <summary>
        /// Ordered cheapest-and-likeliest first. Each rung changes exactly one thing about the
        /// route, so the one that finally works also says what was wrong: a stale circuit, a tor
        /// that went away, or a machine that had none running at all.
        /// </summary>
        static readonly TorRung[] TorLadder =
        [
            new("the known Tor endpoint", Rediscover: false, StartOwnTor: false, OwnSocksClient: false, FreshCircuit: false),
            new("a fresh Tor circuit", Rediscover: false, StartOwnTor: false, OwnSocksClient: false, FreshCircuit: true),
            new("the app's own SOCKS5 client on a fresh circuit", Rediscover: false, StartOwnTor: false, OwnSocksClient: true, FreshCircuit: true),
            new("a rediscovered Tor endpoint", Rediscover: true, StartOwnTor: false, OwnSocksClient: false, FreshCircuit: true),
            new("a rediscovered endpoint with the app's own SOCKS5 client", Rediscover: true, StartOwnTor: false, OwnSocksClient: true, FreshCircuit: true),
            new("a tor started by this app", Rediscover: false, StartOwnTor: true, OwnSocksClient: false, FreshCircuit: true)
        ];

        /// <summary>
        /// Opens the client the way this account is allowed to be opened. A direct connection for
        /// everyone else; for a Tor-only account, the ladder — every rung a different route through
        /// Tor, none of them a route around it.
        /// </summary>
        static async Task<TClient> OpenAsync<TClient>(Func<TClient> createClient, string protocolName, string host, int port,
            ConnectionSecurity security, string username, MailAccountData account, string? passwordOverride,
            CancellationToken cancellationToken) where TClient : MailService
        {
            // Both settled before a circuit is built, and both for the same reason: neither a
            // locked vault nor a cleartext login is worth six attempts to discover. The security
            // mapping can refuse outright here, which is the only place it can be refused once.
            string password = RequirePassword(account, passwordOverride);
            var socketOptions = ToSocketOptions(security, account, host, port);

            if (!account.TorOnly)
            {
                var direct = createClient();
                return await ConnectAndAuthenticateAsync(direct, protocolName, host, port, socketOptions, username, password, cancellationToken);
            }

            var failures = new List<string>();
            try
            {
                foreach (var rung in TorLadder)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        return await OpenThroughTorAsync(createClient, protocolName, host, port, socketOptions, username,
                            account, password, rung, cancellationToken);
                    }
                    // Only the CALLER's cancellation ends the climb. An OperationCanceledException
                    // raised by something else — a budget inside a proxy client, a token a callee
                    // linked for itself — means that route failed, not that the work was called
                    // off, and abandoning the remaining rungs over it was throwing away the very
                    // alternatives the ladder exists to provide.
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (AuthenticationException)
                    {
                        // The tunnel worked; the mailbox said no. Trying five more circuits only
                        // turns one wrong password into six failed logins on the provider's side.
                        throw;
                    }
                    catch (ServiceNotAuthenticatedException)
                    {
                        throw;
                    }
                    catch (TorUnavailableException ex) when (!ex.Recoverable)
                    {
                        // No tor on the machine, or auto-start switched off. Every remaining rung
                        // needs the same thing, so they would all fail the same way.
                        failures.Add($"{rung.Description}: {ex.Message}");
                        throw new TorUnavailableException(Summarize(account, protocolName, failures), recoverable: false);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{rung.Description}: {ex.Message}");
                        Log($"{protocolName} over Tor failed via {rung.Description}: {ex.Message}", LogLevel.Warning);
                    }
                }

                throw new TorUnavailableException(Summarize(account, protocolName, failures));
            }
            finally
            {
                // Written whichever way the attempt ended: a mailbox that only works on the third
                // rung is a mailbox with a problem, and without this line nobody would ever see it.
                if (failures.Count > 0)
                    Log($"Tor route report for {account.EmailAddress} ({protocolName}): {failures.Count} of {TorLadder.Length} route(s) failed. "
                        + string.Join(" | ", failures), LogLevel.Warning);
            }
        }

        static string Summarize(MailAccountData account, string protocolName, List<string> failures) =>
            $"'{account.EmailAddress}' may only be reached through Tor and every route failed for {protocolName}. "
            + string.Join(" | ", failures);

        /// <summary>Builds one rung's route and runs the connect through it.</summary>
        static async Task<TClient> OpenThroughTorAsync<TClient>(Func<TClient> createClient, string protocolName, string host, int port,
            SecureSocketOptions socketOptions, string username, MailAccountData account, string password, TorRung rung,
            CancellationToken cancellationToken) where TClient : MailService
        {
            if (rung.FreshCircuit) TorService.RotateCircuit(account.Id);

            var endpoint = rung.StartOwnTor
                ? await TorService.StartAppManagedAsync(cancellationToken)
                : await TorService.RequireEndpointAsync(cancellationToken, rung.Rediscover);

            var client = createClient();
            try
            {
                client.ProxyClient = TorService.CreateProxy(endpoint, account.Id, rung.OwnSocksClient);
                client.Timeout = TorClientTimeoutMs;
                client.CheckCertificateRevocation = TorRevocationCheck;
                RequireTunnel(client, account);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            Log($"{protocolName} for {account.EmailAddress} routing through {endpoint} via {rung.Description}.");

            // The rung's own budget. Its expiry is NOT the caller's cancellation, so the ladder's
            // filter lets it fall through to the next rung instead of ending the climb.
            using var rungBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            rungBudget.CancelAfter(TorRungBudget);
            try
            {
                return await ConnectAndAuthenticateAsync(client, protocolName, host, port, socketOptions, username, password, rungBudget.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"{rung.Description} did not produce a connection within {TorRungBudget.TotalSeconds:0}s; the next route is tried.");
            }
        }

        public static Task<ImapClient> OpenImapAsync(MailAccountData account, CancellationToken cancellationToken, string? passwordOverride = null) =>
            OpenAsync(static () => new ImapClient(), "IMAP", account.IncomingHost, account.IncomingPort, account.IncomingSecurity,
                account.IncomingUsername, account, passwordOverride, cancellationToken);

        public static Task<Pop3Client> OpenPop3Async(MailAccountData account, CancellationToken cancellationToken, string? passwordOverride = null) =>
            OpenAsync(static () => new Pop3Client(), "POP3", account.IncomingHost, account.IncomingPort, account.IncomingSecurity,
                account.IncomingUsername, account, passwordOverride, cancellationToken);

        public static Task<SmtpClient> OpenSmtpAsync(MailAccountData account, CancellationToken cancellationToken, string? passwordOverride = null) =>
            OpenAsync(static () => new SmtpClient(), "SMTP", account.SmtpHost, account.SmtpPort, account.SmtpSecurity,
                string.IsNullOrWhiteSpace(account.SmtpUsername) ? account.IncomingUsername : account.SmtpUsername,
                account, passwordOverride, cancellationToken);
    }
}
