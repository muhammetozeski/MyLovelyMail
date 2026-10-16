using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Services.Tor;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.Constants;
using GlobalSettings = MyLovelyMail.MainProject.Stores.Settings;
using MyLovelyMail.MainProject.Constants.ThemeConstants;

namespace MyLovelyMail.MainProject.ZTests;

public static partial class DebugApi
{
    /// <summary>Handles the connections endpoints using the application's existing services.</summary>
    /// <param name="request">The accepted local request, including its method and body.</param>
    /// <param name="query">Decoded query values shared with the route dispatcher.</param>
    /// <returns>The response object serialized by the listener.</returns>
    static async Task<object?> HandleConnectionsAsync(HttpListenerRequest request, NameValueCollection query)
    {
        switch (request.HttpMethod, request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant())
        {

                // One rotation step on its own, so a test can watch which folder it picks.
                case ("POST", "/sync/folders"):
                {
                    var account = RequireAccount(RequireQueryValue(query, "accountId"));
                    var before = MessageStore.GetFolders(account.Id).ToDictionary(f => f.FullName, f => f.LastSyncedUtc);
                    await ImapSyncService.SyncAccountAsync(account);
                    return new { refreshed = MessageStore.GetFolders(account.Id)
                        .Where(f => !f.IsLocal && f.LastSyncedUtc != null && before.GetValueOrDefault(f.FullName) != f.LastSyncedUtc)
                        .Select(f => new { f.FullName, f.LastSyncedUtc, CachedCount = MessageStore.CountSummaries(account.Id, f.FullName) }) };
                }

                // Connect-only, never AUTH: this reports what a host answers on, not whether a
                // password works.
                case ("GET", "/connect-diagnose"):
                {
                    string host = RequireQueryValue(query, "host");
                    var protocol = Enum.Parse<ConnectTriageService.MailProtocol>(query["protocol"] ?? "Imap", ignoreCase: true);
                    string stage = query["stage"] ?? "incoming server";
                    var seed = new ConnectDiagnosis(Enum.Parse<DiagnosisKind>(query["kind"] ?? "ConnectionRefused", ignoreCase: true), "Probe.", stage);
                    // ?accountId= makes the probe travel the way that account is allowed to: a
                    // Tor-only one is probed through Tor or not at all.
                    var probeAccount = query["accountId"] is { Length: > 0 } probeAccountId ? RequireAccount(probeAccountId) : null;
                    return await ConnectTriageService.ProbeAsync(seed, protocol, host, account: probeAccount);
                }

                case ("POST", "/sync"):
                    await SyncScheduler.SyncNowAsync();
                    return new { ok = true, syncing = SyncScheduler.IsSyncing };

                // Which accounts hold a live IDLE loop. The set is otherwise invisible, and "the
                // old connection is still up" is exactly the state that needs watching.
                case ("GET", "/idle"):
                    return new { running = ImapIdleService.RunningAccountIds };

                // What TLS the connection path would insist on for this host and port. It is a pure
                // function of (security, TorOnly, host, port) and the difference between two of its
                // answers is whether an exit relay can read the password, so it is worth being able
                // to ask directly instead of inferring it from a connection that has to be made.
                case ("GET", "/tls-choice"):
                {
                    var account = RequireAccount(RequireQueryValue(query, "accountId"));
                    string host = query["host"] ?? account.IncomingHost;
                    int port = int.TryParse(query["port"], out int parsedPort) ? parsedPort : account.IncomingPort;
                    var security = Enum.Parse<ConnectionSecurity>(query["security"] ?? account.IncomingSecurity.ToString(), ignoreCase: true);
                    try
                    {
                        return new
                        {
                            account.TorOnly,
                            host,
                            port,
                            requested = security.ToString(),
                            chosen = MailConnections.ToSocketOptions(security, account, host, port).ToString(),
                            onion = MailConnections.IsOnionHost(host)
                        };
                    }
                    catch (Exception ex)
                    {
                        return new { account.TorOnly, host, port, requested = security.ToString(), refused = ex.Message };
                    }
                }

                // Everything about the Tor route without building a circuit: which port is in use,
                // which executable would be started, what the last attempt said.
                case ("GET", "/tor"):
                    return TorService.Describe();

                // The check that separates Tor from any other SOCKS5 proxy — a RESOLVE over the
                // SOCKS port. ?host= aims it somewhere else; it costs one circuit either way.
                case ("POST", "/tor/check"):
                {
                    var verification = await TorService.VerifyAsync(CancellationToken.None,
                        probeHost: query["host"] ?? "check.torproject.org");
                    return new { verification.IsTor, verification.ProvenNotTor, verification.Detail, resolved = verification.ResolvedAddress?.ToString() };
                }

                // Starts a tor belonging to the app and waits for its bootstrap, which is the one
                // step slow enough that a test needs to trigger it on purpose rather than have it
                // happen inside the first mail connection.
                case ("POST", "/tor/start"):
                {
                    var endpoint = await TorService.StartAppManagedAsync(CancellationToken.None);
                    return new { endpoint = endpoint.ToString(), TorProcess.OwnSocksPort, output = TorProcess.RecentOutput.TakeLast(20) };
                }

                case ("POST", "/tor/stop"):
                {
                    TorProcess.Stop();
                    return new { stopped = true, TorProcess.IsRunning };
                }

                // Opens a real connection the way the sync path does and reports which route
                // carried it. For a Tor-only account this is the end-to-end proof: it either comes
                // back tunnelled or it comes back as a refusal, and never as a direct connection.
                case ("POST", "/tor/connect-test"):
                {
                    var account = RequireAccount(RequireQueryValue(query, "accountId"));
                    string stage = query["stage"] ?? "incoming";
                    var started = DateTime.UtcNow;
                    if (stage == "smtp")
                    {
                        using var smtp = await MailConnections.OpenSmtpAsync(account, CancellationToken.None);
                        await smtp.DisconnectAsync(true);
                    }
                    else if (account.Protocol == IncomingProtocol.Imap)
                    {
                        using var imap = await MailConnections.OpenImapAsync(account, CancellationToken.None);
                        await imap.DisconnectAsync(true);
                    }
                    else
                    {
                        using var pop3 = await MailConnections.OpenPop3Async(account, CancellationToken.None);
                        await pop3.DisconnectAsync(true);
                    }
                    return new
                    {
                        account.EmailAddress,
                        account.TorOnly,
                        stage,
                        // The route THIS connection took, recorded by the connect itself. It used
                        // to report TorService.Current, which is the globally current endpoint at
                        // the moment of the answer — so a mailbox that only got through on the
                        // sixth rung looked identical to one that worked on the first.
                        route = SyncHealthService.For(account.Id).LastRoute,
                        seconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1)
                    };
                }

                case ("GET", "/health"):
                    return AccountStore.Accounts.Select(a => new
                    {
                        a.Id,
                        a.EmailAddress,
                        health = SyncHealthService.For(a.Id)
                    });

                // Marks the account healthy through the same call a successful pass makes. Several
                // screens branch on IsFailing, and a failing account is trivial to arrange but
                // impossible to undo without either a working server or this.
                case ("POST", "/health/clear"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    SyncHealthService.MarkSuccess(accountId);
                    return new { accountId, health = SyncHealthService.For(accountId) };
                }

            default:
                throw new InvalidOperationException($"Unknown endpoint: {request.HttpMethod} {request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant()}");
        }
    }
}