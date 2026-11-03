using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
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
/// Requires STARTTLS and a valid server certificate; there is no cleartext or invalid-certificate fallback.
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
    /// Scrubs identifying metadata and returns the sender's domain for <c>EHLO</c>:
    /// <list type="bullet">
    /// <item><c>Message-Id</c>: 128 random bits at the sender's domain (no host name, process id, counter or clock).</item>
    /// <item><c>Date</c>: UTC (+0000), without the computer's local timezone offset.</item>
    /// <item>Client headers (X-Mailer, User-Agent, X-Originating-IP, X-MimeOLE, Thread-Index, X-MS-*) removed.</item>
    /// <item>Every MIME part: Content-Disposition creation/modification/read dates (local file times in the local offset) removed;
    /// every multipart boundary replaced by a random one without the library's recognisable prefix.</item>
    /// </list>
    /// Applied right before every anonymous send, so a message retried from the Outbox gets fresh values too.
    /// </summary>
    public static string SanitizeMessageForAnonymity(MailAccountData account, MimeMessage message)
    {
        message.From.Clear();
        message.From.Add(new MailboxAddress(account.DisplayName, account.EmailAddress));
        message.Sender = null;

        string senderEmail = message.From.Mailboxes.FirstOrDefault()?.Address ?? account.EmailAddress;
        string senderDomain = ExtractDomain(senderEmail, "mail.tm");

        message.Date = DateTimeOffset.UtcNow;

        message.MessageId = $"{RandomNumberGenerator.GetHexString(32, lowercase: true)}@{senderDomain}";

        message.Headers.RemoveAll(HeaderId.XMailer);
        message.Headers.RemoveAll(HeaderId.UserAgent);
        message.Headers.RemoveAll("X-Originating-IP");
        message.Headers.RemoveAll("X-MimeOLE");
        message.Headers.RemoveAll("Thread-Index");
        message.Headers.RemoveAll("Thread-Topic");
        foreach (var header in message.Headers.Where(static h => h.Field.StartsWith("X-", StringComparison.OrdinalIgnoreCase)
            || h.Field.StartsWith("Resent-", StringComparison.OrdinalIgnoreCase)
            || h.Field.StartsWith("ARC-", StringComparison.OrdinalIgnoreCase)
            || h.Field.Equals("Received", StringComparison.OrdinalIgnoreCase)
            || h.Field.Equals("Return-Path", StringComparison.OrdinalIgnoreCase)
            || h.Field.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase)
            || h.Field.Equals("DKIM-Signature", StringComparison.OrdinalIgnoreCase)
            || h.Field.Equals("Disposition-Notification-To", StringComparison.OrdinalIgnoreCase)
            || h.Field.Equals("Return-Receipt-To", StringComparison.OrdinalIgnoreCase)).ToList())
            message.Headers.Remove(header);

        if (message.Body != null)
            ScrubEntity(message.Body);

        return senderDomain;
    }

    static void ScrubEntity(MimeEntity entity)
    {
        if (entity.ContentDisposition is { } disposition)
        {
            disposition.CreationDate = null;
            disposition.ModificationDate = null;
            disposition.ReadDate = null;
            disposition.Size = null;
        }

        switch (entity)
        {
            case Multipart multipart:
                multipart.Boundary = $"----=_Part_{RandomNumberGenerator.GetHexString(24, lowercase: true)}";
                foreach (var child in multipart)
                    ScrubEntity(child);
                break;
            // A forwarded message is user content; changing its body would break its original
            // cryptographic authentication. Only its outer attachment disposition is normalized.
        }
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

        Log($"DirectMxSmtpService.SendAsync started: recipientCount={allRecipients.Count}.", Run: IsLogEnabled);

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

        Log($"DirectMxSmtpService.SendAsync finished in {sw.ElapsedMilliseconds}ms.", Run: IsLogEnabled);
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
                return ParseMxResponse(domain, json);
            }
            catch (MxDeliveryRefusedException)
            {
                throw;
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

        Log("MX lookup failed through all Tor DNS-over-HTTPS routes.", LogLevel.Warning, Run: IsLogEnabled);
        throw new IOException("No DNS-over-HTTPS resolver returned a successful MX response.");
    }

    sealed class MxDeliveryRefusedException(string message) : InvalidOperationException(message);

    /// <summary>Distinguishes a successful implicit MX answer from a DNS failure or explicit null MX.</summary>
    internal static IReadOnlyList<string> ParseMxResponse(string domain, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("Status", out var status) || !status.TryGetInt32(out int code))
            throw new JsonException("The DNS resolver returned no status.");
        if (code == 3) throw new MxDeliveryRefusedException("The recipient domain does not exist.");
        if (code != 0) throw new IOException($"The DNS resolver returned status {code}.");
        if (!root.TryGetProperty("Answer", out var answers)) return [domain];
        if (answers.ValueKind != JsonValueKind.Array) throw new JsonException("Invalid DNS answer list.");
        var hosts = new List<(int Preference, string Host)>();
        foreach (var answer in answers.EnumerateArray())
        {
            if (!answer.TryGetProperty("type", out var type) || !type.TryGetInt32(out int rrType))
                throw new JsonException("A DNS answer has no record type.");
            if (rrType != 15) continue;
            string[] parts = (answer.GetProperty("data").GetString() ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !ushort.TryParse(parts[0], out ushort preference))
                throw new JsonException("Invalid MX record.");
            if (parts[1] == ".") throw new MxDeliveryRefusedException("The recipient domain explicitly does not accept mail (null MX).");
            string host = parts[1].TrimEnd('.');
            if (Uri.CheckHostName(host) != UriHostNameType.Dns) throw new JsonException("Invalid MX hostname.");
            hosts.Add((preference, host));
        }
        return hosts.Count == 0 ? [domain] : hosts.OrderBy(static h => h.Preference).Select(static h => h.Host)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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

                // Require authenticated TLS: a Tor exit must not read or modify the message.
                foreach (var tlsMode in new[] { SecureSocketOptions.StartTls })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attemptSw = Stopwatch.StartNew();

                    // Every attempt — each MX host, each TLS mode, each rotation — on its own fresh
                    // circuit: never the circuit of the inbox poll, the DoH lookup or another domain.
                    var streamCredentials = TorService.NewStreamCredentials();

                    using var smtp = new TorSmtpClient
                    {
                        Timeout = 35_000,
                        CheckCertificateRevocation = false,
                        LocalDomain = senderDomain,
                        ProxyClient = TorService.CreateProxy(endpoint, streamCredentials)
                    };

                    using var stepBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    stepBudget.CancelAfter(TimeSpan.FromSeconds(30));

                    try
                    {
                        Log($"Direct-to-MX attempting {mxHost}:{SmtpMxPort} (tls={tlsMode}, circuit={epoch}, rotation={rotation + 1}/{MaxCircuitRotationsPerDomain})...", Run: IsLogEnabled);
                        await smtp.ConnectAsync(mxHost, SmtpMxPort, tlsMode, stepBudget.Token);
                        await smtp.SendAsync(message, senderMailbox, recipients, stepBudget.Token);
                        await smtp.DisconnectAsync(true, stepBudget.Token);

                        SyncHealthService.MarkRoute(
                            account.Id,
                            $"{endpoint} Direct-to-MX ({mxHost}:25, {tlsMode}, isolated connection)");
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
