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
using MyLovelyMail.MainProject.Services.Mail.FastMail;
using MyLovelyMail.MainProject.Services.Tor;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.Constants;
using GlobalSettings = MyLovelyMail.MainProject.Stores.Settings;
using MyLovelyMail.MainProject.Constants.ThemeConstants;

namespace MyLovelyMail.MainProject.Diagnostics;

public static partial class DebugApi
{
    sealed class FastMailProvisionBody
    {
        public string ProviderId { get; set; } = "guerrilla";
        public string DesiredUsername { get; set; } = string.Empty;
        public string DesiredDomain { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public bool UseRandomUsername { get; set; } = true;
        public string DisplayName { get; set; } = string.Empty;
        public string FolderId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
    }

    /// <summary>Handles the connections endpoints using the application's existing services.</summary>
    static async Task<object?> HandleConnectionsAsync(HttpListenerRequest request, NameValueCollection query)
    {
        switch (request.HttpMethod, request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant())
        {
            case ("POST", "/sync/folders"):
            {
                var account = RequireAccount(RequireQueryValue(query, "accountId"));
                var before = MessageStore.GetFolders(account.Id).ToDictionary(f => f.FullName, f => f.LastSyncedUtc);
                if (account.Protocol == IncomingProtocol.FastMail)
                    await FastMailSyncService.SyncAccountAsync(account);
                else
                    await ImapSyncService.SyncAccountAsync(account);
                return new
                {
                    refreshed = MessageStore.GetFolders(account.Id)
                        .Where(f => !f.IsLocal && f.LastSyncedUtc != null && before.GetValueOrDefault(f.FullName) != f.LastSyncedUtc)
                        .Select(f => new { f.FullName, f.LastSyncedUtc, CachedCount = MessageStore.CountSummaries(account.Id, f.FullName) })
                };
            }

            case ("GET", "/connect-diagnose"):
            {
                string host = RequireQueryValue(query, "host");
                var protocol = Enum.Parse<ConnectTriageService.MailProtocol>(query["protocol"] ?? "Imap", ignoreCase: true);
                string stage = query["stage"] ?? "incoming server";
                var seed = new ConnectDiagnosis(Enum.Parse<DiagnosisKind>(query["kind"] ?? "ConnectionRefused", ignoreCase: true), "Probe.", stage);
                var probeAccount = query["accountId"] is { Length: > 0 } probeAccountId ? RequireAccount(probeAccountId) : null;
                return await ConnectTriageService.ProbeAsync(seed, protocol, host, account: probeAccount);
            }

            case ("POST", "/sync"):
            {
                string? accountId = query["accountId"];
                await SyncScheduler.SyncNowAsync(accountId: accountId);
                return new { ok = true, accountId, syncing = SyncScheduler.IsSyncing };
            }

            case ("GET", "/fastmail/providers"):
                return FastMailProviderRegistry.All.Select(static p => new
                {
                    p.Id,
                    p.DisplayName,
                    p.Emoji,
                    p.Description,
                    p.ApiHost,
                    p.SupportsCustomUsername,
                    p.SupportsDomainSelection,
                    p.SupportsPassword
                });

            case ("GET", "/fastmail/domains"):
            {
                string providerId = RequireQueryValue(query, "provider");
                var provider = FastMailProviderRegistry.GetById(providerId)
                    ?? throw new InvalidOperationException($"Unknown Fast Mail provider '{providerId}'.");
                string isolationKey = query["isolationKey"] ?? ("probe-" + providerId);
                var domains = await provider.GetDomainsAsync(isolationKey, CancellationToken.None);
                return new { provider = provider.Id, domains };
            }

            case ("POST", "/fastmail/provision"):
            {
                var body = await JsonSerializer.DeserializeAsync<FastMailProvisionBody>(request.InputStream, Json)
                    ?? new FastMailProvisionBody();
                var provider = FastMailProviderRegistry.GetById(body.ProviderId)
                    ?? throw new InvalidOperationException($"Unknown Fast Mail provider '{body.ProviderId}'.");

                string accountId = Guid.NewGuid().ToString("N");
                var provision = await provider.ProvisionAccountAsync(
                    new FastMailProvisionRequest(
                        IsolationKey: accountId,
                        DesiredUsername: body.DesiredUsername,
                        DesiredDomain: body.DesiredDomain,
                        Password: body.Password,
                        UseRandomUsername: body.UseRandomUsername || string.IsNullOrWhiteSpace(body.DesiredUsername)),
                    CancellationToken.None);

                var account = new MailAccountData
                {
                    Id = accountId,
                    DisplayName = body.DisplayName.Trim(),
                    EmailAddress = provision.EmailAddress,
                    FolderId = AccountFolderStore.NormalizeFolderId(body.FolderId),
                    Title = body.Title.Trim(),
                    Note = body.Note.Trim(),
                    Protocol = IncomingProtocol.FastMail,
                    FastMailProviderId = provider.Id,
                    FastMailRemoteAccountId = provision.RemoteAccountId,
                    IncomingHost = provider.ApiHost,
                    IncomingPort = 443,
                    IncomingSecurity = ConnectionSecurity.SslOnConnect,
                    IncomingUsername = provision.Username,
                    SmtpHost = string.Empty,
                    SmtpPort = 25,
                    SmtpSecurity = ConnectionSecurity.StartTls,
                    TorOnly = true,
                    ColorHex = AppColors.IdentityPalette[AccountStore.Accounts.Count % AppColors.IdentityPalette.Length]
                };

                CredentialVault.SetPassword(account.Id, provision.PasswordOrSecret);
                AccountStore.Save(account);
                MessageStore.SaveFolder(new MailFolderData
                {
                    AccountId = account.Id,
                    FullName = MessageStore.InboxFullName,
                    DisplayName = "Inbox",
                    Role = FolderRole.Inbox
                });

                return new
                {
                    account.Id,
                    account.EmailAddress,
                    account.FastMailProviderId,
                    account.FastMailRemoteAccountId,
                    account.IncomingUsername,
                    provision.Domain,
                    account.FolderId,
                    account.Title,
                    account.Note
                };
            }

            case ("GET", "/direct-mx/resolve"):
            {
                string domain = RequireQueryValue(query, "domain");
                string isolationKey = query["accountId"] ?? "direct-mx-probe";
                var mxHosts = await DirectMxSmtpService.ResolveMxOverTorAsync(isolationKey, domain, CancellationToken.None);
                return new { domain, isolationKey, mxHosts };
            }

            case ("POST", "/tests/fastmail"):
                return await RunFastMailEndToEndSuiteAsync();

            case ("GET", "/idle"):
                return new { running = ImapIdleService.RunningAccountIds };

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

            case ("GET", "/tor"):
                return TorService.Describe();

            case ("POST", "/tor/check"):
            {
                var verification = await TorService.VerifyAsync(CancellationToken.None,
                    probeHost: query["host"] ?? "check.torproject.org");
                return new { verification.IsTor, verification.ProvenNotTor, verification.Detail, resolved = verification.ResolvedAddress?.ToString() };
            }

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
                else if (account.Protocol == IncomingProtocol.FastMail)
                {
                    await FastMailSyncService.SyncAccountAsync(account, CancellationToken.None);
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

    public sealed record FastMailTestStepResult(string Step, bool Passed, long ElapsedMs, string Detail);

    /// <summary>
    /// Runs the end-to-end automated test suite for Tor orphan lock cleanup, Guerrilla Mail (provision, send, receive),
    /// and Mail.tm (domains, provision, send, receive) using the application's real services over Tor.
    /// </summary>
    public static async Task<object> RunFastMailEndToEndSuiteAsync(CancellationToken cancellationToken = default)
    {
        var steps = new List<FastMailTestStepResult>();
        string? guerrillaAccountId = null;
        string? mailTmAccountId = null;

        async Task<T> RunStepAsync<T>(string name, Func<Task<(T Result, string Detail)>> action)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Log($"[FastMailTestSuite] STEP START: {name}");
            try
            {
                var (result, detail) = await action();
                steps.Add(new FastMailTestStepResult(name, true, sw.ElapsedMilliseconds, detail));
                Log($"[FastMailTestSuite] STEP PASS ({sw.ElapsedMilliseconds}ms): {name} -> {detail}");
                return result;
            }
            catch (Exception ex)
            {
                steps.Add(new FastMailTestStepResult(name, false, sw.ElapsedMilliseconds, ex.ToString()));
                Log($"[FastMailTestSuite] STEP FAIL ({sw.ElapsedMilliseconds}ms): {name}\n{ex}", LogLevel.Error);
                throw;
            }
        }

        try
        {
            // 1. Tor executable discovery & managed Tor bootstrap (including orphan lock cleanup)
            await RunStepAsync("1. Tor Bootstrap & Orphan Lock Cleanup", async () =>
            {
                var exec = TorProcess.Find() ?? throw new InvalidOperationException("Tor executable not found.");
                var endpoint = await TorService.StartAppManagedAsync(cancellationToken);
                var verification = await TorService.VerifyAsync(cancellationToken, endpoint);
                if (!verification.IsTor)
                    throw new InvalidOperationException($"Managed Tor failed verification: {verification.Detail}");
                return (true, $"Exec='{exec.Path}', Endpoint='{endpoint}', Verified='{verification.Detail}'");
            });

            // 2. Guerrilla Mail account provisioning over Tor
            var guerrillaAccount = await RunStepAsync("2. Guerrilla Mail Provisioning over Tor", async () =>
            {
                var provider = FastMailProviderRegistry.GetById("guerrilla")
                    ?? throw new InvalidOperationException("Guerrilla provider missing.");
                string id = Guid.NewGuid().ToString("N");
                var provision = await provider.ProvisionAccountAsync(
                    new FastMailProvisionRequest(
                        IsolationKey: id,
                        DesiredUsername: string.Empty,
                        DesiredDomain: "sharklasers.com",
                        Password: string.Empty,
                        UseRandomUsername: true),
                    cancellationToken);

                var account = new MailAccountData
                {
                    Id = id,
                    DisplayName = "Guerrilla E2E",
                    EmailAddress = provision.EmailAddress,
                    Protocol = IncomingProtocol.FastMail,
                    FastMailProviderId = provider.Id,
                    FastMailRemoteAccountId = provision.RemoteAccountId,
                    IncomingHost = provider.ApiHost,
                    IncomingPort = 443,
                    IncomingSecurity = ConnectionSecurity.SslOnConnect,
                    IncomingUsername = provision.Username,
                    SmtpHost = string.Empty,
                    SmtpPort = 25,
                    SmtpSecurity = ConnectionSecurity.StartTls,
                    TorOnly = true
                };
                CredentialVault.SetPassword(account.Id, provision.PasswordOrSecret);
                AccountStore.Save(account);
                MessageStore.SaveFolder(new MailFolderData
                {
                    AccountId = account.Id,
                    FullName = MessageStore.InboxFullName,
                    DisplayName = "Inbox",
                    Role = FolderRole.Inbox
                });
                guerrillaAccountId = account.Id;
                return (account, $"Provisioned '{account.EmailAddress}' (id={account.Id})");
            });

            // 3. Mail.tm domain discovery & account provisioning over Tor
            var mailTmAccount = await RunStepAsync("3. Mail.tm Provisioning over Tor", async () =>
            {
                var provider = FastMailProviderRegistry.GetById("mailtm")
                    ?? throw new InvalidOperationException("Mail.tm provider missing.");
                string id = Guid.NewGuid().ToString("N");
                var domains = await provider.GetDomainsAsync(id, cancellationToken);
                if (domains.Count == 0)
                    throw new InvalidOperationException("Mail.tm returned 0 domains.");

                var provision = await provider.ProvisionAccountAsync(
                    new FastMailProvisionRequest(
                        IsolationKey: id,
                        DesiredUsername: string.Empty,
                        DesiredDomain: domains[0],
                        Password: string.Empty,
                        UseRandomUsername: true),
                    cancellationToken);

                var account = new MailAccountData
                {
                    Id = id,
                    DisplayName = "MailTm E2E",
                    EmailAddress = provision.EmailAddress,
                    Protocol = IncomingProtocol.FastMail,
                    FastMailProviderId = provider.Id,
                    FastMailRemoteAccountId = provision.RemoteAccountId,
                    IncomingHost = provider.ApiHost,
                    IncomingPort = 443,
                    IncomingSecurity = ConnectionSecurity.SslOnConnect,
                    IncomingUsername = provision.Username,
                    SmtpHost = string.Empty,
                    SmtpPort = 25,
                    SmtpSecurity = ConnectionSecurity.StartTls,
                    TorOnly = true
                };
                CredentialVault.SetPassword(account.Id, provision.PasswordOrSecret);
                AccountStore.Save(account);
                MessageStore.SaveFolder(new MailFolderData
                {
                    AccountId = account.Id,
                    FullName = MessageStore.InboxFullName,
                    DisplayName = "Inbox",
                    Role = FolderRole.Inbox
                });
                mailTmAccountId = account.Id;
                return (account, $"Provisioned '{account.EmailAddress}' on domain '{domains[0]}' (id={account.Id})");
            });

            // 4. Guerrilla Mail -> Mail.tm Send (Direct-to-MX over Tor) & Mail.tm Receive (Sync + Download)
            string gToMSubject = $"E2E-Guerrilla-To-MailTm-{Guid.NewGuid():N}"[..32];
            string gToMBody = $"Guerrilla Mail anonymous Direct-to-MX send test at {DateTime.UtcNow:O}";

            await RunStepAsync("4. Guerrilla Mail Send (Direct-to-MX over Tor -> Mail.tm)", async () =>
            {
                var draft = new ComposeDraft
                {
                    Account = guerrillaAccount,
                    To = mailTmAccount.EmailAddress,
                    Subject = gToMSubject,
                    Body = gToMBody
                };
                var outcome = await ComposeService.SendAsync(draft);
                if (outcome != SendOutcome.Sent)
                    throw new InvalidOperationException($"ComposeService.SendAsync returned {outcome} (LastFailure: {OutboxService.LastFailure?.Reason})");
                return (outcome, $"Sent '{gToMSubject}' from '{guerrillaAccount.EmailAddress}' to '{mailTmAccount.EmailAddress}', route='{SyncHealthService.For(guerrillaAccount.Id).LastRoute}'");
            });

            await RunStepAsync("5. Mail.tm Receive & Full MIME Download over Tor", async () =>
            {
                MailMessageSummary? landed = null;
                for (int poll = 1; poll <= 12; poll++)
                {
                    await FastMailSyncService.SyncAccountAsync(mailTmAccount, cancellationToken);
                    landed = MessageStore.GetSummaries(mailTmAccount.Id, MessageStore.InboxFullName)
                        .FirstOrDefault(s => s.Subject.Contains(gToMSubject, StringComparison.OrdinalIgnoreCase));
                    if (landed != null) break;
                    await Task.Delay(3000, cancellationToken);
                }

                if (landed == null)
                    throw new InvalidOperationException($"Message '{gToMSubject}' did not arrive in Mail.tm inbox '{mailTmAccount.EmailAddress}' after 12 polls.");

                var mime = await FastMailSyncService.DownloadMessageAsync(mailTmAccount, landed.Uid, cancellationToken);
                var rendered = MailBodyRenderer.Render(mailTmAccount, MessageStore.InboxFullName, landed);
                string bodyText = mime.TextBody ?? rendered?.Html ?? string.Empty;
                if (!bodyText.Contains("Guerrilla Mail anonymous Direct-to-MX", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Downloaded Mail.tm message body did not contain expected text. Actual: '{bodyText}'");

                return (landed.Uid, $"Received UID={landed.Uid}, From='{landed.FromAddress}', Subject='{landed.Subject}', Route='{SyncHealthService.For(mailTmAccount.Id).LastRoute}'");
            });

            // 6. Mail.tm -> Guerrilla Mail Send (Direct-to-MX over Tor) & Guerrilla Mail Receive (Sync + Download)
            string mToGSubject = $"E2E-MailTm-To-Guerrilla-{Guid.NewGuid():N}"[..32];
            string mToGBody = $"Mail.tm anonymous Direct-to-MX send test at {DateTime.UtcNow:O}";

            await RunStepAsync("6. Mail.tm Send (Direct-to-MX over Tor -> Guerrilla Mail)", async () =>
            {
                var draft = new ComposeDraft
                {
                    Account = mailTmAccount,
                    To = guerrillaAccount.EmailAddress,
                    Subject = mToGSubject,
                    Body = mToGBody
                };
                var outcome = await ComposeService.SendAsync(draft);
                if (outcome != SendOutcome.Sent)
                    throw new InvalidOperationException($"ComposeService.SendAsync returned {outcome} (LastFailure: {OutboxService.LastFailure?.Reason})");
                return (outcome, $"Sent '{mToGSubject}' from '{mailTmAccount.EmailAddress}' to '{guerrillaAccount.EmailAddress}', route='{SyncHealthService.For(mailTmAccount.Id).LastRoute}'");
            });

            await RunStepAsync("7. Guerrilla Mail Receive & Body Download over Tor", async () =>
            {
                MailMessageSummary? landed = null;
                for (int poll = 1; poll <= 15; poll++)
                {
                    await FastMailSyncService.SyncAccountAsync(guerrillaAccount, cancellationToken);
                    landed = MessageStore.GetSummaries(guerrillaAccount.Id, MessageStore.InboxFullName)
                        .FirstOrDefault(s => s.Subject.Contains(mToGSubject, StringComparison.OrdinalIgnoreCase));
                    if (landed != null) break;
                    await Task.Delay(3500, cancellationToken);
                }

                if (landed == null)
                {
                    var allInInbox = MessageStore.GetSummaries(guerrillaAccount.Id, MessageStore.InboxFullName);
                    throw new InvalidOperationException(
                        $"Message '{mToGSubject}' did not arrive in Guerrilla Mail inbox '{guerrillaAccount.EmailAddress}' after 15 polls. " +
                        $"Inbox currently has {allInInbox.Count} messages: [{string.Join(", ", allInInbox.Select(s => s.Subject))}]");
                }

                var mime = await FastMailSyncService.DownloadMessageAsync(guerrillaAccount, landed.Uid, cancellationToken);
                var rendered = MailBodyRenderer.Render(guerrillaAccount, MessageStore.InboxFullName, landed);
                string bodyText = (mime.TextBody ?? string.Empty) + " " + (rendered?.Html ?? string.Empty);
                if (!bodyText.Contains("Mail.tm anonymous Direct-to-MX", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Downloaded Guerrilla Mail message body did not contain expected text. Actual: '{bodyText}'");

                return (landed.Uid, $"Received UID={landed.Uid}, From='{landed.FromAddress}', Subject='{landed.Subject}', Route='{SyncHealthService.For(guerrillaAccount.Id).LastRoute}'");
            });
        }
        catch
        {
            // Step failure is already recorded in `steps`.
        }
        finally
        {
            if (guerrillaAccountId != null)
            {
                try { AccountStore.Remove(guerrillaAccountId); } catch { }
            }
            if (mailTmAccountId != null)
            {
                try { AccountStore.Remove(mailTmAccountId); } catch { }
            }
        }

        return new
        {
            allPassed = steps.Count == 7 && steps.All(static s => s.Passed),
            passedCount = steps.Count(static s => s.Passed),
            totalSteps = steps.Count,
            steps
        };
    }
}
