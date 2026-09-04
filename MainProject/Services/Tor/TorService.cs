using System.Net;
using System.Net.Sockets;
using MailKit.Net.Proxy;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Tor
{
    /// <summary>
    /// No Tor route exists right now. A Tor-only account turns this into a refusal to connect —
    /// it is never downgraded into "connect directly instead", which is the whole reason the
    /// account was marked Tor-only.
    /// </summary>
    public sealed class TorUnavailableException(string message, bool recoverable = true) : Exception(message)
    {
        /// <summary>False when trying again cannot help (no tor executable on the machine, auto-start switched off).</summary>
        public bool Recoverable { get; } = recoverable;
    }

    /// <summary>Where a working SOCKS port came from. Worth keeping: it is the difference between "we started it" and "something is listening there".</summary>
    public enum TorEndpointSource
    {
        /// <summary>A tor this app started and watched bootstrap.</summary>
        AppManaged,
        /// <summary>The host/port in the settings.</summary>
        Configured,
        /// <summary>The standard tor daemon port, 9050.</summary>
        SystemDaemon,
        /// <summary>The port a Tor Browser exposes, 9150.</summary>
        TorBrowser
    }

    /// <summary>A SOCKS5 endpoint that answered a greeting, and how it was found.</summary>
    public sealed record TorEndpoint(string Host, int Port, TorEndpointSource Source)
    {
        public override string ToString() => $"{Host}:{Port} ({Source})";
    }

    /// <summary>The outcome of asking the SOCKS port to resolve a name the Tor way.</summary>
    /// <param name="IsTor">True only when the port answered a Tor-specific command as Tor does.</param>
    /// <param name="ProvenNotTor">
    /// True when the port <em>proved</em> it is something else (it rejected the Tor command).
    /// Both flags false means the check could not reach a verdict — usually the network was out —
    /// and the answer must not be remembered either way.
    /// </param>
    public sealed record TorVerification(bool IsTor, string Detail, bool ProvenNotTor = false, IPAddress? ResolvedAddress = null);

    /// <summary>
    /// The one place that answers "can this app reach the Tor network, and how". Everything a
    /// Tor-only account needs goes through here: finding a SOCKS port, starting tor when there is
    /// none, handing out proxy clients, and rotating an account onto a fresh circuit.
    /// </summary>
    public static class TorService
    {
        /// <summary>Standard ports, tried after the configured one: a tor daemon and a running Tor Browser.</summary>
        const int DaemonSocksPort = 9050;
        const int BrowserSocksPort = 9150;

        /// <summary>A greeting on a loopback socket either answers at once or is not there at all.</summary>
        static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

        /// <summary>Resolving a name over Tor is a real circuit build, so the check gets a real budget.</summary>
        static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(60);

        static readonly SemaphoreSlim discoveryGate = new(1, 1);

        /// <summary>The endpoint the last successful discovery settled on; re-probed before reuse.</summary>
        public static TorEndpoint? Current { get; private set; }

        /// <summary>Why the last discovery failed, kept for the settings card and the debug API.</summary>
        public static string? LastError { get; private set; }

        /// <summary>Result of the last <see cref="VerifyAsync"/>, so the UI can show it without re-running a circuit build.</summary>
        public static TorVerification? LastVerification { get; private set; }

        #region Circuit isolation

        /// <summary>
        /// Per-isolation-key circuit counter. Tor puts SOCKS streams with different
        /// username/password pairs on different circuits (IsolateSOCKSAuth), so bumping this
        /// number is how the app asks for a new path after a circuit misbehaves — no control port,
        /// no NEWNYM, and no effect on any other account's traffic.
        /// </summary>
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> circuitEpochs = new();

        public static int CircuitEpochOf(string isolationKey) => circuitEpochs.GetOrAdd(isolationKey, 1);

        /// <summary>Moves one isolation key onto a fresh circuit; the next connection takes a different path.</summary>
        public static int RotateCircuit(string isolationKey)
        {
            int epoch = circuitEpochs.AddOrUpdate(isolationKey, 2, static (_, current) => current + 1);
            Log($"Tor circuit rotated for '{isolationKey}' (epoch {epoch}).");
            return epoch;
        }

        /// <summary>
        /// The SOCKS login that pins this key to its own circuit. Tor never checks these; the pair
        /// is an isolation token, which is why the "password" is just the epoch counter.
        /// </summary>
        public static NetworkCredential IsolationCredentials(string isolationKey) =>
            new($"mlm-{isolationKey}", CircuitEpochOf(isolationKey).ToString());

        #endregion

        #region Endpoint discovery

        /// <summary>
        /// Verdicts about ports this session has already asked, so the circuit-building RESOLVE
        /// check runs once per port rather than once per connection. False entries are the
        /// valuable ones: a port that proved it is NOT Tor is never offered again.
        /// </summary>
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> provenTor = new();

        /// <summary>
        /// A SOCKS5 endpoint that is <em>proven</em> to be Tor, or an exception. Never returns
        /// something unverified: for a Tor-only account, handing back a plain SOCKS proxy that
        /// happens to sit on port 9050 would put the account on the open network under a name that
        /// says otherwise, which is the one failure this whole feature exists to prevent.
        /// </summary>
        public static async Task<TorEndpoint> RequireEndpointAsync(CancellationToken cancellationToken, bool forceRediscovery = false)
        {
            await discoveryGate.WaitAsync(cancellationToken);
            try
            {
                if (!forceRediscovery && Current is { } cached
                    && await SpeaksSocks5Async(cached.Host, cached.Port, cancellationToken))
                    return cached;

                Current = null;
                var refusals = new List<string>();

                foreach (var candidate in Candidates())
                {
                    try
                    {
                        if (!await SpeaksSocks5Async(candidate.Host, candidate.Port, cancellationToken))
                        {
                            refusals.Add($"{candidate}: nothing speaking SOCKS5 there");
                            continue;
                        }

                        if (!await IsProvenTorAsync(candidate, cancellationToken))
                        {
                            refusals.Add($"{candidate}: answers SOCKS5 but did not prove it is Tor");
                            continue;
                        }

                        Log($"Tor SOCKS endpoint accepted: {candidate}");
                        LastError = null;
                        return Current = candidate;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // One dead candidate is the normal case; the next one is the answer.
                        refusals.Add($"{candidate}: {ex.Message}");
                        Log($"Tor candidate {candidate} did not answer: {ex.Message}");
                    }
                }

                Log($"No existing Tor endpoint qualified ({string.Join("; ", refusals)}).", LogLevel.Warning);
                return Current = await StartOwnTorAsync(cancellationToken);
            }
            finally
            {
                discoveryGate.Release();
            }
        }

        /// <summary>
        /// Whether this endpoint really is Tor. A tor this app started and watched print
        /// "Bootstrapped 100%" needs no probe — its identity is not in question. Anything else has
        /// to answer Tor's RESOLVE command, which a general-purpose SOCKS5 proxy cannot.
        /// </summary>
        static async Task<bool> IsProvenTorAsync(TorEndpoint endpoint, CancellationToken cancellationToken)
        {
            if (endpoint.Source == TorEndpointSource.AppManaged) return true;

            string key = $"{endpoint.Host}:{endpoint.Port}";
            if (provenTor.TryGetValue(key, out bool remembered)) return remembered;

            var verification = await VerifyAsync(cancellationToken, endpoint);
            // Only the two definite answers are remembered. A check that failed because the
            // network was down says nothing about the port, and caching it would keep a perfectly
            // good tor daemon rejected for the rest of the session.
            if (verification.IsTor || verification.ProvenNotTor) provenTor[key] = verification.IsTor;
            return verification.IsTor;
        }

        /// <summary>Candidates in order of how much the user meant them: our own tor, the configured port, then the two standard ones.</summary>
        static IEnumerable<TorEndpoint> Candidates()
        {
            string host = Settings.TorSocksHost.Value.Trim() is { Length: > 0 } configured ? configured : "127.0.0.1";

            if (TorProcess.OwnSocksPort is { } own)
                yield return new TorEndpoint("127.0.0.1", own, TorEndpointSource.AppManaged);

            int configuredPort = Settings.TorSocksPort.Value;
            if (configuredPort is > 0 and < 65536)
                yield return new TorEndpoint(host, configuredPort, TorEndpointSource.Configured);

            if (configuredPort != DaemonSocksPort)
                yield return new TorEndpoint("127.0.0.1", DaemonSocksPort, TorEndpointSource.SystemDaemon);

            if (configuredPort != BrowserSocksPort)
                yield return new TorEndpoint("127.0.0.1", BrowserSocksPort, TorEndpointSource.TorBrowser);
        }

        /// <summary>
        /// Starts a tor belonging to this app and makes it the current endpoint, whatever else may
        /// be listening. The last rung of the connection ladder: when every port on the machine
        /// either refuses or turns out not to be Tor, this is the way that does not depend on
        /// anything the machine already had running.
        /// </summary>
        public static async Task<TorEndpoint> StartAppManagedAsync(CancellationToken cancellationToken)
        {
            await discoveryGate.WaitAsync(cancellationToken);
            try
            {
                return Current = await StartOwnTorAsync(cancellationToken);
            }
            finally
            {
                discoveryGate.Release();
            }
        }

        /// <summary>
        /// Starts a tor of the app's own — the last resort, and the only branch that can install
        /// nothing and therefore fail permanently. Nested deliberately: a first attempt, then one
        /// retry against a tor that may already have come up while the first attempt was waiting.
        /// </summary>
        static async Task<TorEndpoint> StartOwnTorAsync(CancellationToken cancellationToken)
        {
            if (!Settings.TorAutoStart.Value)
            {
                LastError = "No SOCKS proxy answered and starting one is switched off (TorAutoStart).";
                throw new TorUnavailableException(LastError, recoverable: false);
            }

            try
            {
                int port = await TorProcess.StartAsync(cancellationToken);
                var started = new TorEndpoint("127.0.0.1", port, TorEndpointSource.AppManaged);
                LastError = null;
                return started;
            }
            catch (TorUnavailableException ex)
            {
                // The bootstrap watch can time out while tor is in fact one step from ready; the
                // port itself is the better judge, so it gets asked before the failure stands.
                if (TorProcess.OwnSocksPort is { } port && await SpeaksSocks5Async("127.0.0.1", port, cancellationToken))
                {
                    Log($"Tor's bootstrap watch gave up, but its SOCKS port {port} answers. Using it.", LogLevel.Warning);
                    LastError = null;
                    return new TorEndpoint("127.0.0.1", port, TorEndpointSource.AppManaged);
                }

                LastError = ex.Message;
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                throw new TorUnavailableException($"Tor could not be started: {ex.Message}");
            }
        }

        /// <summary>
        /// Whether a SOCKS5 server answers a greeting there. Purely local — no circuit is built and
        /// nothing leaves the machine, so this is cheap enough to run before every connection.
        /// </summary>
        public static async Task<bool> SpeaksSocks5Async(string host, int port, CancellationToken cancellationToken)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(ProbeTimeout);
            try
            {
                using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(host, port, budget.Token);
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                await TorSocks5.HandshakeAsync(stream, null, budget.Token);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Proxy clients

        /// <summary>
        /// The proxy MailKit connects through. Two implementations answer the same endpoint:
        /// MailKit's own (used first — it is the one with the miles on it) and
        /// <see cref="TorSocks5Client"/>. Both send the host name to Tor as DOMAINNAME, so neither
        /// resolves the mail server on this machine.
        /// </summary>
        public static IProxyClient CreateProxy(TorEndpoint endpoint, string isolationKey, bool useOwnClient)
        {
            var credentials = IsolationCredentials(isolationKey);
            return useOwnClient
                ? new TorSocks5Client(endpoint.Host, endpoint.Port, credentials)
                : new Socks5Client(endpoint.Host, endpoint.Port, credentials);
        }

        #endregion

        #region Verification

        /// <summary>
        /// Asks the SOCKS port to resolve a name using Tor's RESOLVE command (0xF0). This is the
        /// one question whose answer separates Tor from any other SOCKS5 proxy: a general-purpose
        /// proxy must reject the unknown command with 0x07, while Tor resolves the name over a
        /// circuit and returns an address. A pass therefore proves three things at once — the port
        /// is Tor, Tor has bootstrapped, and it can reach the network.
        /// </summary>
        public static async Task<TorVerification> VerifyAsync(CancellationToken cancellationToken, TorEndpoint? endpoint = null,
            string probeHost = "check.torproject.org")
        {
            if (endpoint == null)
            {
                try
                {
                    // Discovery verifies on its own way through, so this branch is the UI's
                    // "check it now" button rather than anything the connection path calls.
                    endpoint = await RequireEndpointAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    return LastVerification = new TorVerification(false, ex.Message);
                }
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(VerifyTimeout);
            try
            {
                using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(endpoint.Host, endpoint.Port, budget.Token);
                await using var stream = new NetworkStream(socket, ownsSocket: false);

                await TorSocks5.HandshakeAsync(stream, IsolationCredentials("verify"), budget.Token);
                var address = await TorSocks5.SendRequestAsync(stream, TorSocks5.CommandTorResolve, probeHost, 0, budget.Token);

                return LastVerification = new TorVerification(true,
                    $"{endpoint} resolved {probeHost} over Tor" + (address == null ? "." : $" to {address}."),
                    ProvenNotTor: false, ResolvedAddress: address);
            }
            catch (SocksReplyException ex) when (ex.ReplyCode == TorSocks5.ReplyCommandNotSupported)
            {
                return LastVerification = new TorVerification(false,
                    $"{endpoint} is a SOCKS5 proxy but rejected Tor's RESOLVE command — it is not Tor.", ProvenNotTor: true);
            }
            catch (Exception ex)
            {
                return LastVerification = new TorVerification(false, $"{endpoint} did not answer the Tor check: {ex.Message}");
            }
        }

        #endregion

        /// <summary>Everything the settings card and the debug API show, gathered without touching the network.</summary>
        public static object Describe() => new
        {
            endpoint = Current?.ToString(),
            source = Current?.Source.ToString(),
            appManagedProcessRunning = TorProcess.IsRunning,
            appManagedSocksPort = TorProcess.OwnSocksPort,
            executable = TorProcess.Find()?.Path,
            autoStart = Settings.TorAutoStart.Value,
            configured = $"{Settings.TorSocksHost.Value}:{Settings.TorSocksPort.Value}",
            lastError = LastError,
            lastVerification = LastVerification is { } verification ? new { verification.IsTor, verification.Detail } : null,
            portVerdicts = provenTor.Select(static v => new { port = v.Key, isTor = v.Value }),
            torOnlyAccounts = AccountStore.Accounts.Where(static a => a.TorOnly).Select(static a => a.EmailAddress),
            recentTorOutput = TorProcess.RecentOutput.TakeLast(20)
        };
    }
}
