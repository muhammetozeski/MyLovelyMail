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

    // No session state is kept: every operation claims a brand-new sid_token without sending any
    // earlier one, uses it for its single call and drops it. A cached or persisted sid_token linked
    // every poll of the inbox across circuits, rotations and restarts.

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

        Log($"GuerrillaMail.ProvisionAccountAsync started: useRandom={request.UseRandomUsername}.", Run: IsLogEnabled);

        try
        {
            using var client = TorService.CreateIsolatedHttpClient(request.IsolationKey, new CookieContainer());

            string sidToken;
            string emailUser;
            if (!request.UseRandomUsername && !string.IsNullOrWhiteSpace(request.DesiredUsername))
            {
                emailUser = SanitizeUsername(request.DesiredUsername);
                Log("GuerrillaMail.ProvisionAccountAsync: claiming a custom address.", Run: IsLogEnabled);
                sidToken = await ClaimUsernameAsync(client, emailUser, cancellationToken);
            }
            else
            {
                string url = $"{EndpointUrl}?f=get_email_address&lang=en";
                Log($"GuerrillaMail.ProvisionAccountAsync: requesting random address via get_email_address...", Run: IsLogEnabled);
                string json = await client.GetStringAsync(url, cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string addr = root.TryGetProperty("email_addr", out var addrProp) ? addrProp.GetString() ?? string.Empty : string.Empty;
                int atIndex = addr.IndexOf('@');
                emailUser = atIndex > 0 ? addr[..atIndex] : ("anon" + System.Security.Cryptography.RandomNumberGenerator.GetInt32(10000, 99999));
                Log("GuerrillaMail.ProvisionAccountAsync: confirming a random address.", Run: IsLogEnabled);
                sidToken = await ClaimUsernameAsync(client, emailUser, cancellationToken);
            }

            string fullEmail = $"{emailUser}@{domain}";
            Log($"GuerrillaMail.ProvisionAccountAsync succeeded: email='{fullEmail}', elapsed={sw.ElapsedMilliseconds}ms", Run: IsLogEnabled);
            // The sid_token is neither returned nor stored anywhere (account.json, vault).
            return new FastMailProvisionResult(fullEmail, emailUser, domain, string.Empty, string.Empty);
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
            using var client = TorService.CreateIsolatedHttpClient(account.Id, new CookieContainer());
            string sidToken = await EnsureSessionAsync(client, account, forceReclaim: false, cancellationToken);

            var items = new List<FastMailMessageItem>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            int offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Offsets and the total count are native API pagination; the session stays in memory
                // for this operation and every page uses the same isolated Tor client.
                string url = $"{EndpointUrl}?f=get_email_list&offset={offset}&sid_token={Uri.EscapeDataString(sidToken)}";
                using var response = await client.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                var expected = new Uri(EndpointUrl);
                if (response.RequestMessage?.RequestUri is not { } responseUri
                    || responseUri.Scheme != expected.Scheme || responseUri.Host != expected.Host
                    || responseUri.Port != expected.Port || responseUri.AbsolutePath != expected.AbsolutePath)
                    throw new InvalidOperationException("Guerrilla Mail returned an unexpected message-page origin or path.");
                string json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("list", out var listElement) || listElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("Guerrilla Mail returned no message list.");

                int added = 0;
                foreach (var entry in listElement.EnumerateArray())
                {
                    string mailId = ReadStringOrNumber(entry, "mail_id");
                    if (string.IsNullOrWhiteSpace(mailId) || !seenIds.Add(mailId)) continue;
                    added++;

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
                int pageCount = listElement.GetArrayLength();
                if (pageCount == 0) break;
                if (added == 0) throw new InvalidOperationException("Guerrilla Mail repeated a message page without advancing.");
                offset = checked(offset + pageCount);
                if (root.TryGetProperty("count", out _) && offset >= ReadLong(root, "count")) break;
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
            using var client = TorService.CreateIsolatedHttpClient(account.Id, new CookieContainer());
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

            Log($"GuerrillaMail.DownloadMessageAsync finished: bodyChars={body.Length}, elapsed={sw.ElapsedMilliseconds}ms", Run: IsLogEnabled);
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
            using var client = TorService.CreateIsolatedHttpClient(account.Id, new CookieContainer());
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

    /// <summary>A brand-new session for this one operation: set_email_user with no earlier sid_token.</summary>
    static Task<string> EnsureSessionAsync(HttpClient client, MailAccountData account, bool forceReclaim, CancellationToken cancellationToken)
    {
        string emailUser = ExtractLocalPart(account.EmailAddress, account.IncomingUsername);
        Log($"GuerrillaMail: claiming a one-operation session, retry={forceReclaim}.", Run: IsLogEnabled);
        return ClaimUsernameAsync(client, emailUser, cancellationToken);
    }

    static async Task<string> ClaimUsernameAsync(HttpClient client, string emailUser, CancellationToken cancellationToken)
    {
        string initialJson = await client.GetStringAsync($"{EndpointUrl}?f=get_email_address&lang=en", cancellationToken);
        using var initial = JsonDocument.Parse(initialJson);
        string initialToken = ReadString(initial.RootElement, "sid_token");
        if (string.IsNullOrWhiteSpace(initialToken))
            throw new InvalidOperationException("Guerrilla Mail did not create a session.");
        var form = new List<KeyValuePair<string, string>>
        {
            new("f", "set_email_user"),
            new("email_user", emailUser),
            new("sid_token", initialToken),
            new("lang", "en")
        };

        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(EndpointUrl, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("sid_token", out var sidProp) && sidProp.GetString() is { Length: > 0 } sid)
            return sid;

        throw new InvalidOperationException("Guerrilla Mail did not return a session token (sid_token).");
    }

    static string SanitizeUsername(string raw)
    {
        string trimmed = raw.Trim();
        int at = trimmed.IndexOf('@');
        if (at > 0) trimmed = trimmed[..at];
        string cleaned = Regex.Replace(trimmed, @"[^a-zA-Z0-9._\-]", "");
        return string.IsNullOrWhiteSpace(cleaned) ? MailTmProvider.GenerateRandomUsername() : cleaned.ToLowerInvariant();
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
