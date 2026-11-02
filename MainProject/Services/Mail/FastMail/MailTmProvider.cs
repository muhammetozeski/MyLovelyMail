using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MimeKit;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services.Tor;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail.FastMail;

/// <summary>
/// Fast Mail provider for Mail.tm (<c>https://api.mail.tm</c>), routed strictly over the account's
/// isolated Tor SOCKS5 circuit. Supports permanent accounts with custom or generated passwords,
/// domain discovery, raw MIME source downloads (<c>/sources/{id}</c>), read state, and deletion.
/// </summary>
public sealed class MailTmProvider : IFastMailProvider
{
    const string BaseUrl = "https://api.mail.tm";

    sealed record TokenEntry(string Token, string AccountRemoteId, DateTime ObtainedUtc);

    static readonly ConcurrentDictionary<string, TokenEntry> tokensByKey = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(45);

    public string Id => "mailtm";
    public string DisplayName => "Mail.tm";
    public string Emoji => "⚡";
    public string Description => "Real password-protected account with raw MIME download over Tor.";
    public string ApiHost => "api.mail.tm";

    public bool SupportsCustomUsername => true;
    public bool SupportsDomainSelection => true;
    public bool SupportsPassword => true;

    public async Task<IReadOnlyList<string>> GetDomainsAsync(string isolationKey, CancellationToken cancellationToken)
    {
        using var client = TorService.CreateIsolatedHttpClient(isolationKey);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/domains");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ld+json"));

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var domains = new List<string>();

        foreach (var item in EnumerateCollection(doc.RootElement))
        {
            bool isActive = !item.TryGetProperty("isActive", out var activeProp) || activeProp.ValueKind != JsonValueKind.False;
            if (!isActive) continue;

            if (item.TryGetProperty("domain", out var domProp) && domProp.GetString() is { Length: > 0 } domain)
                domains.Add(domain.Trim().ToLowerInvariant());
        }

        return domains;
    }

    public async Task<FastMailProvisionResult> ProvisionAccountAsync(FastMailProvisionRequest request, CancellationToken cancellationToken)
    {
        string domain = request.DesiredDomain?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(domain))
        {
            var domains = await GetDomainsAsync(request.IsolationKey, cancellationToken);
            domain = domains.FirstOrDefault()
                ?? throw new InvalidOperationException("Mail.tm returned no active domains.");
        }

        string username = !request.UseRandomUsername && !string.IsNullOrWhiteSpace(request.DesiredUsername)
            ? SanitizeUsername(request.DesiredUsername)
            : GenerateRandomUsername();

        string password = !string.IsNullOrWhiteSpace(request.Password)
            ? request.Password
            : GenerateSecurePassword();

        string email = $"{username}@{domain}";

        using var client = TorService.CreateIsolatedHttpClient(request.IsolationKey);
        string payload = JsonSerializer.Serialize(new { address = email, password });

        using (var createReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/accounts")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        })
        {
            createReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ld+json"));
            using var createResp = await client.SendAsync(createReq, cancellationToken);

            // 201 Created = new account created; 422 Unprocessable = address already exists (user may be logging into their existing Mail.tm account).
            if (!createResp.IsSuccessStatusCode && createResp.StatusCode != HttpStatusCode.UnprocessableEntity)
            {
                string detail = await createResp.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Mail.tm rejected account creation ({(int)createResp.StatusCode}): {ExtractErrorMessage(detail)}");
            }
        }

        var tokenEntry = await ObtainTokenAsync(client, email, password, cancellationToken);
        tokensByKey[request.IsolationKey] = tokenEntry;

        return new FastMailProvisionResult(
            EmailAddress: email,
            Username: email,
            Domain: domain,
            RemoteAccountId: tokenEntry.AccountRemoteId,
            PasswordOrSecret: password);
    }

    public async Task<IReadOnlyList<FastMailMessageItem>> ListMessagesAsync(MailAccountData account, CancellationToken cancellationToken)
    {
        using var client = TorService.CreateIsolatedHttpClient(account.Id);
        string token = await EnsureBearerTokenAsync(client, account, forceRefresh: false, cancellationToken);

        using var response = await SendAuthorizedAsync(client, account, HttpMethod.Get, $"{BaseUrl}/messages?page=1", null, token, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(json);
        var result = new List<FastMailMessageItem>();

        foreach (var item in EnumerateCollection(doc.RootElement))
        {
            string id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;

            string msgId = ReadString(item, "msgid").Trim('<', '>', ' ');
            if (string.IsNullOrWhiteSpace(msgId))
                msgId = $"{id}@mail.tm";

            string fromName = string.Empty;
            string fromAddress = string.Empty;
            if (item.TryGetProperty("from", out var fromObj) && fromObj.ValueKind == JsonValueKind.Object)
            {
                fromName = ReadString(fromObj, "name");
                fromAddress = ReadString(fromObj, "address");
            }

            var toList = new List<string>();
            if (item.TryGetProperty("to", out var toArr) && toArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var recipient in toArr.EnumerateArray())
                {
                    if (recipient.ValueKind == JsonValueKind.Object)
                    {
                        string addr = ReadString(recipient, "address");
                        if (!string.IsNullOrWhiteSpace(addr)) toList.Add(addr);
                    }
                }
            }

            string subject = ReadString(item, "subject");
            string intro = ReadString(item, "intro");
            bool seen = item.TryGetProperty("seen", out var seenProp) && seenProp.ValueKind == JsonValueKind.True;
            bool hasAttachments = item.TryGetProperty("hasAttachments", out var attProp) && attProp.ValueKind == JsonValueKind.True;
            long size = item.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out long s) ? s : 0;

            DateTime dateUtc = DateTime.UtcNow;
            if (DateTimeOffset.TryParse(ReadString(item, "createdAt"), out var parsedDate))
                dateUtc = parsedDate.UtcDateTime;

            result.Add(new FastMailMessageItem(
                RemoteId: id,
                MessageIdHeader: msgId,
                FromName: fromName,
                FromAddress: fromAddress,
                ToAddresses: toList.Count > 0 ? string.Join(", ", toList) : account.EmailAddress,
                Subject: subject,
                PreviewText: intro,
                DateUtc: dateUtc,
                IsSeen: seen,
                HasAttachments: hasAttachments,
                SizeBytes: size));
        }

        return result;
    }

    public async Task<MimeMessage> DownloadMessageAsync(MailAccountData account, string remoteId, CancellationToken cancellationToken)
    {
        using var client = TorService.CreateIsolatedHttpClient(account.Id);
        string token = await EnsureBearerTokenAsync(client, account, forceRefresh: false, cancellationToken);

        // First try /sources/{id}, which returns the full RFC 5322 raw MIME message.
        try
        {
            using var sourceResp = await SendAuthorizedAsync(client, account, HttpMethod.Get, $"{BaseUrl}/sources/{Uri.EscapeDataString(remoteId)}", null, token, cancellationToken);
            string sourceJson = await sourceResp.Content.ReadAsStringAsync(cancellationToken);
            using var sourceDoc = JsonDocument.Parse(sourceJson);
            string rawMime = ReadString(sourceDoc.RootElement, "data");
            if (!string.IsNullOrWhiteSpace(rawMime))
            {
                await using var rawStream = new MemoryStream(Encoding.UTF8.GetBytes(rawMime));
                return await MimeMessage.LoadAsync(rawStream, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Log($"Mail.tm /sources/{remoteId} fallback to /messages/{remoteId}: {ex.Message}", LogLevel.Warning);
        }

        // Fallback: build MimeMessage from /messages/{id} JSON fields.
        using var msgResp = await SendAuthorizedAsync(client, account, HttpMethod.Get, $"{BaseUrl}/messages/{Uri.EscapeDataString(remoteId)}", null, token, cancellationToken);
        string msgJson = await msgResp.Content.ReadAsStringAsync(cancellationToken);
        using var msgDoc = JsonDocument.Parse(msgJson);
        var root = msgDoc.RootElement;

        var message = new MimeMessage();
        string msgId = ReadString(root, "msgid").Trim('<', '>', ' ');
        message.MessageId = string.IsNullOrWhiteSpace(msgId) ? $"{remoteId}@mail.tm" : msgId;
        message.Subject = ReadString(root, "subject");

        if (root.TryGetProperty("from", out var fromObj) && fromObj.ValueKind == JsonValueKind.Object)
        {
            string name = ReadString(fromObj, "name");
            string addr = ReadString(fromObj, "address");
            if (!string.IsNullOrWhiteSpace(addr))
                message.From.Add(new MailboxAddress(name, addr));
        }

        if (root.TryGetProperty("to", out var toArr) && toArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in toArr.EnumerateArray())
            {
                if (r.ValueKind == JsonValueKind.Object && ReadString(r, "address") is { Length: > 0 } addr)
                    message.To.Add(new MailboxAddress(ReadString(r, "name"), addr));
            }
        }

        if (DateTimeOffset.TryParse(ReadString(root, "createdAt"), out var dt))
            message.Date = dt;
        else
            message.Date = DateTimeOffset.UtcNow;

        string textBody = ReadString(root, "text");
        var htmlParts = new StringBuilder();
        if (root.TryGetProperty("html", out var htmlProp))
        {
            if (htmlProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in htmlProp.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String)
                        htmlParts.AppendLine(part.GetString());
                }
            }
            else if (htmlProp.ValueKind == JsonValueKind.String)
            {
                htmlParts.Append(htmlProp.GetString());
            }
        }

        var builder = new BodyBuilder
        {
            TextBody = textBody,
            HtmlBody = htmlParts.Length > 0 ? htmlParts.ToString() : null
        };
        message.Body = builder.ToMessageBody();
        return message;
    }

    public async Task MarkReadAsync(MailAccountData account, IReadOnlyList<string> remoteIds, bool seen, CancellationToken cancellationToken)
    {
        if (remoteIds.Count == 0) return;

        using var client = TorService.CreateIsolatedHttpClient(account.Id);
        string token = await EnsureBearerTokenAsync(client, account, forceRefresh: false, cancellationToken);
        string bodyJson = JsonSerializer.Serialize(new { seen });

        foreach (string id in remoteIds)
        {
            using var content = new StringContent(bodyJson, Encoding.UTF8, "application/merge-patch+json");
            using var resp = await SendAuthorizedAsync(client, account, HttpMethod.Patch, $"{BaseUrl}/messages/{Uri.EscapeDataString(id)}", content, token, cancellationToken);
        }
    }

    public async Task DeleteMessagesAsync(MailAccountData account, IReadOnlyList<string> remoteIds, CancellationToken cancellationToken)
    {
        if (remoteIds.Count == 0) return;

        using var client = TorService.CreateIsolatedHttpClient(account.Id);
        string token = await EnsureBearerTokenAsync(client, account, forceRefresh: false, cancellationToken);

        foreach (string id in remoteIds)
        {
            using var resp = await SendAuthorizedAsync(client, account, HttpMethod.Delete, $"{BaseUrl}/messages/{Uri.EscapeDataString(id)}", null, token, cancellationToken);
        }
    }

    static async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpClient client,
        MailAccountData account,
        HttpMethod method,
        string url,
        HttpContent? content,
        string token,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ld+json"));

        var resp = await client.SendAsync(req, cancellationToken);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            string refreshed = await EnsureBearerTokenAsync(client, account, forceRefresh: true, cancellationToken);
            using var retryReq = new HttpRequestMessage(method, url);
            if (content != null)
            {
                string bytes = await content.ReadAsStringAsync(cancellationToken);
                retryReq.Content = new StringContent(bytes, Encoding.UTF8, content.Headers.ContentType?.MediaType ?? "application/json");
            }
            retryReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);
            retryReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ld+json"));
            resp = await client.SendAsync(retryReq, cancellationToken);
        }

        resp.EnsureSuccessStatusCode();
        return resp;
    }

    static async Task<string> EnsureBearerTokenAsync(HttpClient client, MailAccountData account, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh &&
            tokensByKey.TryGetValue(account.Id, out var cached) &&
            DateTime.UtcNow - cached.ObtainedUtc < TokenLifetime &&
            !string.IsNullOrWhiteSpace(cached.Token))
        {
            return cached.Token;
        }

        string? password = CredentialVault.GetPassword(account.Id);
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(CredentialVault.IsUnlocked
                ? $"No password stored in vault for Mail.tm account '{account.EmailAddress}'."
                : "The credential vault is locked.");

        var entry = await ObtainTokenAsync(client, account.EmailAddress, password, cancellationToken);
        tokensByKey[account.Id] = entry;
        return entry.Token;
    }

    static async Task<TokenEntry> ObtainTokenAsync(HttpClient client, string email, string password, CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new { address = email, password });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/token")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await client.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            string err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(resp.StatusCode == HttpStatusCode.Unauthorized
                ? $"Mail.tm authentication failed for '{email}' (address already registered with a different password, or invalid credentials)."
                : $"Mail.tm token request failed ({(int)resp.StatusCode}): {ExtractErrorMessage(err)}");
        }

        string json = await resp.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        string token = ReadString(doc.RootElement, "token");
        string remoteId = ReadString(doc.RootElement, "id");

        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Mail.tm returned an empty authentication token.");

        return new TokenEntry(token, remoteId, DateTime.UtcNow);
    }

    public static string GenerateSecurePassword(int length = 20)
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%&*";
        char[] chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }

    public static string GenerateRandomUsername()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        char[] buf = new char[10];
        for (int i = 0; i < buf.Length; i++)
            buf[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        return new string(buf);
    }

    static string SanitizeUsername(string raw)
    {
        string trimmed = raw.Trim();
        int at = trimmed.IndexOf('@');
        if (at > 0) trimmed = trimmed[..at];
        string cleaned = Regex.Replace(trimmed, @"[^a-zA-Z0-9._\-]", "");
        return string.IsNullOrWhiteSpace(cleaned) ? GenerateRandomUsername() : cleaned.ToLowerInvariant();
    }

    static IEnumerable<JsonElement> EnumerateCollection(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in root.EnumerateArray())
                yield return el;
            yield break;
        }
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("hydra:member", out var hydra) && hydra.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in hydra.EnumerateArray())
                    yield return el;
                yield break;
            }
            if (root.TryGetProperty("member", out var member) && member.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in member.EnumerateArray())
                    yield return el;
            }
        }
    }

    static string ExtractErrorMessage(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return "Unknown error";
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("hydra:description", out var desc) && desc.GetString() is { Length: > 0 } d)
                return d;
            if (root.TryGetProperty("detail", out var det) && det.GetString() is { Length: > 0 } dt)
                return dt;
            if (root.TryGetProperty("message", out var msg) && msg.GetString() is { Length: > 0 } m)
                return m;
        }
        catch { }
        return rawJson.Length > 200 ? rawJson[..200] : rawJson;
    }

    static string ReadString(JsonElement element, string propName) =>
        element.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;
}
