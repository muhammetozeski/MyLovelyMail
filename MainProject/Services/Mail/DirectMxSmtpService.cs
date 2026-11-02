using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services.Tor;

namespace MyLovelyMail.MainProject.Services.Mail;

/// <summary>
/// Anonymous Direct-to-MX SMTP delivery strictly over Tor.
/// <para>
/// Requires no SMTP relay account: for each recipient domain, MX records are resolved over Tor
/// via DNS-over-HTTPS on the account's isolated circuit (never touching the OS DNS resolver), and
/// the message is delivered directly to the recipient's MX server on port 25 over Tor.
/// </para>
/// <para>
/// Supports both STARTTLS-capable and plain-SMTP MX servers (<see cref="SecureSocketOptions.StartTlsWhenAvailable"/>
/// with fallback to <see cref="SecureSocketOptions.None"/> if a server advertises a broken TLS stack),
/// scrubs local machine hostnames and local timezone offsets from headers and <c>EHLO</c>, and
/// automatically rotates the account's Tor circuit when an exit relay blocks port 25.
/// </para>
/// </summary>
public static class DirectMxSmtpService
{
    const bool IsLogEnabled = true;
    const int SmtpMxPort = 25;
    const int MaxCircuitRotationsPerDomain = 8;

    static readonly string[] DohEndpoints =
    [
        "https://cloudflare-dns.com/dns-query",
        "https://dns.google/resolve"
    ];

    /// <summary>
    /// Scrubs identifying metadata (local machine name in <c>Message-Id</c>, local timezone offset
    /// in <c>Date</c>, and client headers) and returns the sender's domain for <c>EHLO</c>.
    /// </summary>
    public static string SanitizeMessageForAnonymity(MailAccountData account, MimeMessage message)
    {
        if (message.From.Count == 0)
            message.From.Add(new MailboxAddress(account.DisplayName, account.EmailAddress));

        string senderEmail = message.From.Mailboxes.FirstOrDefault()?.Address ?? account.EmailAddress;
        string senderDomain = ExtractDomain(senderEmail, "mail.tm");

        // Always use UTC (+00:00) so the local system timezone is never disclosed in the Date header.
        message.Date = new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero);

        // Always generate a Message-Id scoped to the sender domain so Environment.MachineName never leaks.
        message.MessageId = $"{Guid.NewGuid():N}@{senderDomain}";

        message.Headers.RemoveAll(HeaderId.XMailer);
        message.Headers.RemoveAll(HeaderId.UserAgent);
        message.Headers.RemoveAll("X-Originating-IP");

        return senderDomain;
    }

    public static async Task SendAsync(MailAccountData account, MimeMessage message, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        string senderDomain = SanitizeMessageForAnonymity(account, message);
        var senderMailbox = message.From.Mailboxes.FirstOrDefault()
            ?? new MailboxAddress(account.DisplayName, account.EmailAddress);

        var allRecipients = message.To.Mailboxes
            .Concat(message.Cc.Mailboxes)
            .Concat(message.Bcc.Mailboxes)
            .Where(static m => !string.IsNullOrWhiteSpace(m.Address))
            .DistinctBy(static m => m.Address, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (allRecipients.Count == 0)
            throw new InvalidOperationException("No recipient addresses specified on the outgoing message.");

        Log($"DirectMxSmtpService.SendAsync started: from='{senderMailbox.Address}' (EHLO '{senderDomain}'), recipients=[{string.Join(", ", allRecipients.Select(r => r.Address))}], subject='{message.Subject}'", Run: IsLogEnabled);

        var byDomain = allRecipients
            .GroupBy(static m => ExtractDomain(m.Address, string.Empty), StringComparer.OrdinalIgnoreCase)
            .Where(static g => !string.IsNullOrWhiteSpace(g.Key))
            .ToList();

        foreach (var group in byDomain)
        {
            string recipientDomain = group.Key;
            var domainRecipients = group.ToList();

            var mxHosts = await ResolveMxOverTorAsync(account.Id, recipientDomain, cancellationToken);
            Log($"Direct-to-MX over Tor for '{recipientDomain}': resolved MX [{string.Join(", ", mxHosts)}]", Run: IsLogEnabled);

            await DeliverToDomainOverTorAsync(account, senderDomain, senderMailbox, domainRecipients, mxHosts, message, cancellationToken);
        }

        Log($"DirectMxSmtpService.SendAsync finished in {sw.ElapsedMilliseconds}ms for subject='{message.Subject}'.", Run: IsLogEnabled);
    }

    /// <summary>
    /// Resolves MX records for <paramref name="domain"/> strictly over the account's isolated Tor circuit
    /// using DNS-over-HTTPS JSON queries. Falls back to the domain itself per RFC 5321 Section 5.1 if no MX exists.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ResolveMxOverTorAsync(string isolationKey, string domain, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        using var client = TorService.CreateIsolatedHttpClient(isolationKey, timeout: TimeSpan.FromSeconds(30));

        foreach (string endpoint in DohEndpoints)
        {
            try
            {
                string url = $"{endpoint}?name={Uri.EscapeDataString(domain)}&type=MX";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));

                using var resp = await client.SendAsync(req, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    Log($"DoH MX lookup via '{endpoint}' for '{domain}' returned HTTP {(int)resp.StatusCode}.", LogLevel.Warning, Run: IsLogEnabled);
                    continue;
                }

                string json = await resp.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
                    continue;

                var parsed = new List<(int Preference, string Host)>();
                foreach (var ans in answers.EnumerateArray())
                {
                    if (ans.TryGetProperty("type", out var typeProp) && typeProp.TryGetInt32(out int rrType) && rrType != 15)
                        continue;

                    string? data = ans.TryGetProperty("data", out var dataProp) ? dataProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(data)) continue;

                    string[] parts = data.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[0], out int pref))
                    {
                        string host = parts[1].TrimEnd('.');
                        if (!string.IsNullOrWhiteSpace(host) && host != ".")
                            parsed.Add((pref, host));
                    }
                    else if (parts.Length == 1)
                    {
                        string host = parts[0].TrimEnd('.');
                        if (!string.IsNullOrWhiteSpace(host) && host != ".")
                            parsed.Add((10, host));
                    }
                }

                if (parsed.Count > 0)
                {
                    var hosts = parsed
                        .OrderBy(static x => x.Preference)
                        .Select(static x => x.Host)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    Log($"DoH MX lookup via '{endpoint}' for '{domain}' succeeded in {sw.ElapsedMilliseconds}ms: [{string.Join(", ", hosts)}]", Run: IsLogEnabled);
                    return hosts;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"DoH MX lookup via '{endpoint}' for '{domain}' failed over Tor:\n{ex}", LogLevel.Warning, Run: IsLogEnabled);
            }
        }

        Log($"DoH MX lookup for '{domain}' found no MX records; falling back to A/AAAA host '{domain}'.", LogLevel.Warning, Run: IsLogEnabled);
        return [domain];
    }

    static async Task DeliverToDomainOverTorAsync(
        MailAccountData account,
        string senderDomain,
        MailboxAddress senderMailbox,
        IReadOnlyList<MailboxAddress> recipients,
        IReadOnlyList<string> mxHosts,
        MimeMessage message,
        CancellationToken cancellationToken)
    {
        var attemptErrors = new List<string>();

        for (int rotation = 0; rotation < MaxCircuitRotationsPerDomain; rotation++)
        {
            if (rotation > 0)
                TorService.RotateCircuit(account.Id);

            var endpoint = await TorService.RequireEndpointAsync(cancellationToken);
            int epoch = TorService.CircuitEpochOf(account.Id);

            foreach (string mxHost in mxHosts)
            {
                bool exitNodeBlockedPort25 = false;

                // Try StartTlsWhenAvailable first; if the server has a broken TLS implementation, also try plain port 25.
                foreach (var tlsMode in new[] { SecureSocketOptions.StartTlsWhenAvailable, SecureSocketOptions.None })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attemptSw = Stopwatch.StartNew();

                    using var smtp = new SmtpClient
                    {
                        Timeout = 35_000,
                        CheckCertificateRevocation = false,
                        // Opportunistic TLS on port 25 (RFC 7435): many MX servers present self-signed or
                        // umbrella host certificates; accept them rather than dropping to cleartext or failing.
                        ServerCertificateValidationCallback = static (_, _, _, _) => true,
                        LocalDomain = senderDomain,
                        ProxyClient = TorService.CreateProxy(endpoint, account.Id, useOwnClient: true)
                    };

                    using var stepBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    stepBudget.CancelAfter(TimeSpan.FromSeconds(30));

                    try
                    {
                        Log($"Direct-to-MX attempting {mxHost}:{SmtpMxPort} (tls={tlsMode}, circuit={epoch}, rotation={rotation + 1}/{MaxCircuitRotationsPerDomain})...", Run: IsLogEnabled);
                        await smtp.ConnectAsync(mxHost, SmtpMxPort, tlsMode, stepBudget.Token);
                        await smtp.SendAsync(message, senderMailbox, recipients, stepBudget.Token);
                        await smtp.DisconnectAsync(true, stepBudget.Token);

                        string exitInfo = TorService.ActiveExitLeases.TryGetValue(account.Id, out var lease)
                            ? $", exit {lease.ExitIp}"
                            : string.Empty;
                        SyncHealthService.MarkRoute(
                            account.Id,
                            $"{endpoint} Direct-to-MX ({mxHost}:25, {tlsMode}, circuit {epoch}{exitInfo})");
                        Log($"Direct-to-MX delivered to {mxHost}:25 over Tor ({tlsMode}, circuit {epoch}) in {attemptSw.ElapsedMilliseconds}ms for [{string.Join(", ", recipients.Select(r => r.Address))}].", Run: IsLogEnabled);
                        return;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (SocksReplyException socksEx) when (socksEx.ReplyCode == 0x02)
                    {
                        // ReplyCode 0x02 = "connection not allowed by ruleset" (this Tor exit node blocks port 25).
                        attemptErrors.Add($"{mxHost} (circuit {epoch}): {socksEx.Message}");
                        Log($"Direct-to-MX: Tor circuit {epoch} exit policy blocked port 25 on {mxHost} ({socksEx.Message}); rotating circuit.", LogLevel.Warning, Run: IsLogEnabled);
                        exitNodeBlockedPort25 = true;
                        break;
                    }
                    catch (Exception ex) when (tlsMode == SecureSocketOptions.StartTlsWhenAvailable &&
                                               ex is not (SocksReplyException or SocksProtocolException or SocketException or TimeoutException or OperationCanceledException))
                    {
                        attemptErrors.Add($"{mxHost} (STARTTLS, circuit {epoch}): {ex.Message}");
                        Log($"Direct-to-MX: STARTTLS negotiation failed on {mxHost}:25 (circuit {epoch}), falling back to plain SMTP on same host:\n{ex}", LogLevel.Warning, Run: IsLogEnabled);
                        // Fall through to SecureSocketOptions.None on the same MX host.
                    }
                    catch (Exception ex)
                    {
                        attemptErrors.Add($"{mxHost} (circuit {epoch}): {ex.Message}");
                        Log($"Direct-to-MX attempt on {mxHost}:25 (tls={tlsMode}, circuit {epoch}) failed after {attemptSw.ElapsedMilliseconds}ms:\n{ex}", LogLevel.Warning, Run: IsLogEnabled);
                        break; // Move to next MX host or rotate Tor circuit.
                    }
                }

                if (exitNodeBlockedPort25)
                    break; // Skip remaining MX hosts on this circuit since this exit blocks port 25 globally.
            }
        }

        string finalError =
            $"Direct-to-MX delivery over Tor could not reach '{string.Join(", ", mxHosts)}' on port 25 across {MaxCircuitRotationsPerDomain} Tor circuits " +
            $"(many Tor exit nodes restrict outbound port 25 or recipient MX servers reject Tor exit IPs). Details: {string.Join(" | ", attemptErrors.TakeLast(4))}";
        Log(finalError, LogLevel.Error, Run: IsLogEnabled);
        throw new InvalidOperationException(finalError);
    }

    static string ExtractDomain(string email, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(email))
        {
            int at = email.LastIndexOf('@');
            if (at >= 0 && at + 1 < email.Length)
                return email[(at + 1)..].Trim().ToLowerInvariant();
        }
        return fallback;
    }
}
