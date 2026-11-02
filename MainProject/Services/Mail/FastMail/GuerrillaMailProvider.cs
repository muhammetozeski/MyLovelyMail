using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services.Tor;

namespace MyLovelyMail.MainProject.Services.Mail.FastMail;

/// <summary>
/// Fast Mail provider for Guerrilla Mail (<c>https://api.guerrillamail.com/ajax.php</c>),
/// routed strictly over the account's isolated Tor SOCKS5 circuit.
/// <para>
/// Guerrilla Mail sessions expire after ~18 minutes of inactivity, but calling <c>set_email_user</c>
/// with the account's local username re-attaches the session to the exact same inbox permanently.
/// </para>
/// </summary>
public sealed class GuerrillaMailProvider : IFastMailProvider
{
    const bool IsLogEnabled = true;
    const string EndpointUrl = "https://api.guerrillamail.com/ajax.php";

    static readonly string[] KnownDomains =
    [
        "guerrillamail.com",
        "sharklasers.com",
        "grr.la",
        "guerrillamail.net",
        "guerrillamail.org",
        "guerrillamail.de",
        "guerrillamail.biz",
        "guerrillamail.info",
        "guerrillamailblock.com",
        "pokemail.net",
        "spam4.me"
    ];

    sealed record SessionState(string SidToken, string EmailUser, DateTime ClaimedUtc);

    static readonly ConcurrentDictionary<string, SessionState> sessionsByKey = new(StringComparer.OrdinalIgnoreCase);

    static readonly TimeSpan SessionRefreshWindow = TimeSpan.FromMinutes(10);

    public string Id => "guerrilla";
    public string DisplayName => "Guerrilla Mail";
    public string Emoji => "🦍";
    public string Description => "Passwordless permanent claimable inbox via set_email_user over Tor.";
    public string ApiHost => "api.guerrillamail.com";

    public bool SupportsCustomUsername => true;
    public bool SupportsDomainSelection => true;
    public bool SupportsPassword => false;

    public Task<IReadOnlyList<string>> GetDomainsAsync(string isolationKey, CancellationToken cancellationToken)
    {
        Log($"GuerrillaMail.GetDomainsAsync: isolationKey='{isolationKey}', returning {KnownDomains.Length} static domains.", Run: IsLogEnabled);
        return Task.FromResult<IReadOnlyList<string>>(KnownDomains);
    }

    public async Task<FastMailProvisionResult> ProvisionAccountAsync(FastMailProvisionRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        string domain = !string.IsNullOrWhiteSpace(request.DesiredDomain)
            ? request.DesiredDomain.Trim().ToLowerInvariant()
            : KnownDomains[0];

        Log($"GuerrillaMail.ProvisionAccountAsync started: isolationKey='{request.IsolationKey}', useRandom={request.UseRandomUsername}, desiredUser='{request.DesiredUsername}', domain='{domain}'", Run: IsLogEnabled);

        try
        {
            using var client = TorService.CreateIsolatedHttpClient(request.IsolationKey);

            string sidToken;
            string emailUser;
            if (!request.UseRandomUsername && !string.IsNullOrWhiteSpace(request.DesiredUsername))
            {
                emailUser = SanitizeUsername(request.DesiredUsername);
                Log($"GuerrillaMail.ProvisionAccountAsync: claiming custom username '{emailUser}'...", Run: IsLogEnabled);
                sidToken = await ClaimUsernameAsync(client, emailUser, null, cancellationToken);
            }
            else
            {
                string url = $"{EndpointUrl}?f=get_email_address&lang=en";
                Log($"GuerrillaMail.ProvisionAccountAsync: requesting random address via get_email_address...", Run: IsLogEnabled);
                string json = await client.GetStringAsync(url, cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                sidToken = root.TryGetProperty("sid_token", out var sidProp) ? sidProp.GetString() ?? string.Empty : string.Empty;
                string addr = root.TryGetProperty("email_addr", out var addrProp) ? addrProp.GetString() ?? string.Empty : string.Empty;
                int atIndex = addr.IndexOf('@');
                emailUser = atIndex > 0 ? addr[..atIndex] : ("anon" + Random.Shared.Next(10000, 99999));
                Log($"GuerrillaMail.ProvisionAccountAsync: received random user '{emailUser}', confirming via set_email_user...", Run: IsLogEnabled);
                sidToken = await ClaimUsernameAsync(client, emailUser, sidToken, cancellationToken);
            }

            sessionsByKey[request.IsolationKey] = new SessionState(sidToken, emailUser, DateTime.UtcNow);
            string fullEmail = $"{emailUser}@{domain}";
            Log($"GuerrillaMail.ProvisionAccountAsync succeeded: email='{fullEmail}', elapsed={sw.ElapsedMilliseconds}ms", Run: IsLogEnabled);
            return new FastMailProvisionResult(fullEmail, emailUser, domain, sidToken, sidToken);
        }
        catch (Exception ex)
        {
            Log($"GuerrillaMail.ProvisionAccountAsync failed after {sw.ElapsedMilliseconds}ms:\n{ex}", LogLevel.Error, Run: IsLogEnabled);
            throw;
        }
    }

    public async Task<IReadOnlyList<FastMailMessageItem>> ListMessagesAsync(MailAccountData account, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        Log($"GuerrillaMail.ListMessagesAsync started: account='{account.EmailAddress}' ({account.Id})", Run: IsLogEnabled);

        try
        {
            using var client = TorService.CreateIsolatedHttpClient(account.Id);
            string sidToken = await EnsureSessionAsync(client, account, forceReclaim: false, cancellationToken);

            // Use get_email_list&offset=0 to list all messages currently in the inbox (not just seq > 0).
            string url = $"{EndpointUrl}?f=get_email_list&offset=0&sid_token={Uri.EscapeDataString(sidToken)}";
            string json = await client.GetStringAsync(url, cancellationToken);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("sid_token", out var updatedSid) && updatedSid.GetString() is { Length: > 0 } nextSid)
                UpdateCachedSid(account, nextSid);

            var items = new List<FastMailMessageItem>();
            if (!root.TryGetProperty("list", out var listElement) || listElement.ValueKind != JsonValueKind.Array)
            {
                Log($"GuerrillaMail.ListMessagesAsync: no 'list' array in response for '{account.EmailAddress}', elapsed={sw.ElapsedMilliseconds}ms", LogLevel.Warning, Run: IsLogEnabled);
                return items;
            }

            foreach (var entry in listElement.EnumerateArray())
            {
                string mailId = ReadStringOrNumber(entry, "mail_id");
                if (string.IsNullOrWhiteSpace(mailId)) continue;

                string rawFrom = ReadString(entry, "mail_from");
                string subject = WebUtility.HtmlDecode(ReadString(entry, "mail_subject"));
                string excerpt = WebUtility.HtmlDecode(ReadString(entry, "mail_excerpt"));
                string readStr = ReadStringOrNumber(entry, "mail_read");
                string attStr = ReadStringOrNumber(entry, "att");
                long timestamp = ReadLong(entry, "mail_timestamp");

                ParseFrom(rawFrom, out string fromName, out string fromAddress);
                DateTime dateUtc = timestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime
                    : DateTime.UtcNow;

                items.Add(new FastMailMessageItem(
                    RemoteId: mailId,
                    MessageIdHeader: $"guerrilla-{mailId}@{ExtractDomain(account.EmailAddress)}",
                    FromName: fromName,
                    FromAddress: fromAddress,
                    ToAddresses: account.EmailAddress,
                    Subject: subject,
                    PreviewText: excerpt,
                    DateUtc: dateUtc,
                    IsSeen: readStr == "1",
                    HasAttachments: attStr != "0" && !string.IsNullOrEmpty(attStr),
                    SizeBytes: ReadLong(entry, "mail_size")));
            }

            Log($"GuerrillaMail.ListMessagesAsync finished: account='{account.EmailAddress}', count={items.Count}, elapsed={sw.ElapsedMilliseconds}ms", Run: IsLogEnabled);
            return items;
        }
        catch (Exception ex)
        {
            Log($"GuerrillaMail.ListMessagesAsync failed for '{account.EmailAddress}' after {sw.ElapsedMilliseconds}ms:\n{ex}", LogLevel.Error, Run: IsLogEnabled);
            throw;
        }
    }

    public async Task<MimeMessage> DownloadMessageAsync(MailAccountData account, string remoteId, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        Log($"GuerrillaMail.DownloadMessageAsync started: account='{account.EmailAddress}', remoteId='{remoteId}'", Run: IsLogEnabled);

        try
        {
            using var client = TorService.CreateIsolatedHttpClient(account.Id);
            string sidToken = await EnsureSessionAsync(client, account, forceReclaim: false, cancellationToken);

            string url = $"{EndpointUrl}?f=fetch_email&email_id={Uri.EscapeDataString(remoteId)}&sid_token={Uri.EscapeDataString(sidToken)}";
            string json = await client.GetStringAsync(url, cancellationToken);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.False || !root.TryGetProperty("mail_id", out _))
            {
                Log($"GuerrillaMail.DownloadMessageAsync: initial fetch_email for '{remoteId}' returned no mail_id; forcing session reclaim.", LogLevel.Warning, Run: IsLogEnabled);
                sidToken = await EnsureSessionAsync(client, account, forceReclaim: true, cancellationToken);
                url = $"{EndpointUrl}?f=fetch_email&email_id={Uri.EscapeDataString(remoteId)}&sid_token={Uri.EscapeDataString(sidToken)}";
                json = await client.GetStringAsync(url, cancellationToken);
            }

            using var retryDoc = JsonDocument.Parse(json);
            var mail = retryDoc.RootElement;

            string rawFrom = ReadString(mail, "mail_from");
            string subject = WebUtility.HtmlDecode(ReadString(mail, "mail_subject"));
            string body = ReadString(mail, "mail_body");
            string excerpt = WebUtility.HtmlDecode(ReadString(mail, "mail_excerpt"));
            long timestamp = ReadLong(mail, "mail_timestamp");

            ParseFrom(rawFrom, out string fromName, out string fromAddress);

            var message = new MimeMessage();
            message.MessageId = $"guerrilla-{remoteId}@{ExtractDomain(account.EmailAddress)}";
            message.From.Add(new MailboxAddress(fromName, string.IsNullOrWhiteSpace(fromAddress) ? "unknown@guerrillamail.com" : fromAddress));
            if (MailboxAddress.TryParse(account.EmailAddress, out var toMailbox))
                message.To.Add(toMailbox);
            message.Subject = subject;
            message.Date = timestamp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(timestamp)
                : DateTimeOffset.UtcNow;

            string unwrappedHtml = UnwrapBlockedImages(body);
            var builder = new BodyBuilder
            {
                HtmlBody = string.IsNullOrWhiteSpace(unwrappedHtml) ? null : unwrappedHtml,
                TextBody = StripHtmlToPlainText(string.IsNullOrWhiteSpace(unwrappedHtml) ? excerpt : unwrappedHtml)
            };
            message.Body = builder.ToMessageBody();

            Log($"GuerrillaMail.DownloadMessageAsync finished: account='{account.EmailAddress}', remoteId='{remoteId}', subject='{subject}', bodyChars={body.Length}, elapsed={sw.ElapsedMilliseconds}ms", Run: IsLogEnabled);
            return message;
        }
        catch (Exception ex)
        {
            Log($"GuerrillaMail.DownloadMessageAsync failed for '{account.EmailAddress}', remoteId='{remoteId}' after {sw.ElapsedMilliseconds}ms:\n{ex}", LogLevel.Error, Run: IsLogEnabled);
            throw;
        }
    }

    public Task MarkReadAsync(MailAccountData account, IReadOnlyList<string> remoteIds, bool seen, CancellationToken cancellationToken)
    {
        // Guerrilla Mail marks messages read on fetch_email; local state is persisted in MessageStore.
        return Task.CompletedTask;
    }

    public async Task DeleteMessagesAsync(MailAccountData account, IReadOnlyList<string> remoteIds, CancellationToken cancellationToken)
    {
        if (remoteIds.Count == 0) return;
        var sw = Stopwatch.StartNew();
        Log($"GuerrillaMail.DeleteMessagesAsync started: account='{account.EmailAddress}', count={remoteIds.Count}", Run: IsLogEnabled);

        try
        {
            using var client = TorService.CreateIsolatedHttpClient(account.Id);
            string sidToken = await EnsureSessionAsync(client, account, forceReclaim: false, cancellationToken);

            var formPairs = new List<KeyValuePair<string, string>>
            {
                new("f", "del_email"),
                new("sid_token", sidToken)
            };
            foreach (string id in remoteIds)
                formPairs.Add(new("email_ids[]", id));

            using var content = new FormUrlEncodedContent(formPairs);
            using var response = await client.PostAsync(EndpointUrl, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            Log($"GuerrillaMail.DeleteMessagesAsync finished: account='{account.EmailAddress}', elapsed={sw.ElapsedMilliseconds}ms", Run: IsLogEnabled);
        }
        catch (Exception ex)
        {
            Log($"GuerrillaMail.DeleteMessagesAsync failed for '{account.EmailAddress}' after {sw.ElapsedMilliseconds}ms:\n{ex}", LogLevel.Error, Run: IsLogEnabled);
            throw;
        }
    }

    static async Task<string> EnsureSessionAsync(HttpClient client, MailAccountData account, bool forceReclaim, CancellationToken cancellationToken)
    {
        string emailUser = ExtractLocalPart(account.EmailAddress, account.IncomingUsername);
        if (!forceReclaim &&
            sessionsByKey.TryGetValue(account.Id, out var cached) &&
            string.Equals(cached.EmailUser, emailUser, StringComparison.OrdinalIgnoreCase) &&
            DateTime.UtcNow - cached.ClaimedUtc < SessionRefreshWindow &&
            !string.IsNullOrWhiteSpace(cached.SidToken))
        {
            return cached.SidToken;
        }

        string? initialSid = sessionsByKey.TryGetValue(account.Id, out var prev) ? prev.SidToken : account.FastMailRemoteAccountId;
        Log($"GuerrillaMail.EnsureSessionAsync: claiming/refreshing session for user='{emailUser}', forceReclaim={forceReclaim}", Run: IsLogEnabled);
        string sidToken = await ClaimUsernameAsync(client, emailUser, initialSid, cancellationToken);
        sessionsByKey[account.Id] = new SessionState(sidToken, emailUser, DateTime.UtcNow);
        return sidToken;
    }

    static async Task<string> ClaimUsernameAsync(HttpClient client, string emailUser, string? existingSid, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("f", "set_email_user"),
            new("email_user", emailUser),
            new("lang", "en")
        };
        if (!string.IsNullOrWhiteSpace(existingSid))
            form.Add(new("sid_token", existingSid));

        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(EndpointUrl, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("sid_token", out var sidProp) && sidProp.GetString() is { Length: > 0 } sid)
            return sid;

        throw new InvalidOperationException("Guerrilla Mail did not return a session token (sid_token).");
    }

    static void UpdateCachedSid(MailAccountData account, string sidToken)
    {
        string emailUser = ExtractLocalPart(account.EmailAddress, account.IncomingUsername);
        sessionsByKey[account.Id] = new SessionState(sidToken, emailUser, DateTime.UtcNow);
    }

    static string SanitizeUsername(string raw)
    {
        string trimmed = raw.Trim();
        int at = trimmed.IndexOf('@');
        if (at > 0) trimmed = trimmed[..at];
        string cleaned = Regex.Replace(trimmed, @"[^a-zA-Z0-9._\-]", "");
        return string.IsNullOrWhiteSpace(cleaned) ? "user" + Random.Shared.Next(1000, 9999) : cleaned.ToLowerInvariant();
    }

    static string ExtractLocalPart(string email, string fallbackUser)
    {
        if (!string.IsNullOrWhiteSpace(email))
        {
            int at = email.IndexOf('@');
            if (at > 0) return email[..at].Trim();
        }
        return !string.IsNullOrWhiteSpace(fallbackUser) ? fallbackUser.Trim() : "user";
    }

    static string ExtractDomain(string email)
    {
        int at = email.IndexOf('@');
        return at > 0 && at + 1 < email.Length ? email[(at + 1)..].Trim() : KnownDomains[0];
    }

    static void ParseFrom(string rawFrom, out string name, out string address)
    {
        if (MailboxAddress.TryParse(rawFrom, out var parsed))
        {
            name = parsed.Name ?? string.Empty;
            address = parsed.Address;
            return;
        }
        name = string.Empty;
        address = rawFrom.Trim();
    }

    static string UnwrapBlockedImages(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        return Regex.Replace(
            html,
            @"/res\.php\?[^""'\s>]*?\bq=([^""'&\s>]+)",
            static match => WebUtility.UrlDecode(match.Groups[1].Value),
            RegexOptions.IgnoreCase);
    }

    static string StripHtmlToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        string withBreaks = Regex.Replace(html, @"<(br|p|/p|div|/div|tr|/tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
        string stripped = Regex.Replace(withBreaks, @"<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(stripped).Trim();
    }

    static string ReadString(JsonElement element, string propName) =>
        element.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;

    static string ReadStringOrNumber(JsonElement element, string propName)
    {
        if (!element.TryGetProperty(propName, out var prop)) return string.Empty;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString() ?? string.Empty,
            JsonValueKind.Number => prop.GetRawText(),
            _ => string.Empty
        };
    }

    static long ReadLong(JsonElement element, string propName)
    {
        if (!element.TryGetProperty(propName, out var prop)) return 0;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out long val)) return val;
        if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out long parsed)) return parsed;
        return 0;
    }
}
