using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using System.Runtime.Versioning;
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

namespace MyLovelyMail.MainProject.Diagnostics
{
    /// <summary>Debug-only loopback endpoints for inspecting and driving the app through its existing services. RouteAsync dispatches to groups under Diagnostics/DebugApi.</summary>
    [UnsupportedOSPlatform("browser")]
    public static partial class DebugApi
    {
        public const string Prefix = "http://127.0.0.1:52539/";

        static readonly JsonSerializerOptions Json = new()
        {
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        static HttpListener? listener;

        public static void Start()
        {
            if (listener != null) return;
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add(Prefix);
                listener.Start();
                _ = Task.Run(ListenLoopAsync);
                Log($"DebugApi listening on {Prefix}");
            }
            catch (Exception ex)
            {
                Log($"DebugApi could not start: {ex.Message}", LogLevel.Error);
                listener = null;
            }
        }

        static async Task ListenLoopAsync()
        {
            while (listener is { IsListening: true })
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch
                {
                    return;
                }
                _ = Task.Run(() => HandleAsync(context));
            }
        }

        static async Task HandleAsync(HttpListenerContext context)
        {
            object? result;
            int status = 200;
            if (RefusalReason(context.Request) is { } refusal)
            {
                result = new { error = refusal };
                status = 403;
                Log($"DebugApi refused a request: {refusal}", LogLevel.Warning);
            }
            else
            {
                try
                {
                    result = await RouteAsync(context.Request);
                }
                catch (Exception ex)
                {
                    status = 500;
                    result = new { error = ex.Message };
                }
            }

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(result, Json);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentEncoding = Encoding.UTF8;
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        }

        /// <summary>The only Host values a request to this listener may carry. Anything else was addressed to some other name that resolved here.</summary>
        static readonly string[] AllowedHosts = ["127.0.0.1:52539", "[::1]:52539", "localhost:52539"];

        /// <summary>Rejects browser-originated requests and non-loopback Host values. Loopback binding alone does not prevent a browser page from sending requests.</summary>
        static string? RefusalReason(HttpListenerRequest request)
        {
            if (request.Headers["Origin"] is { Length: > 0 } origin)
                return $"a browser page sent this (Origin: {origin}). This API is for test scripts, not for pages.";

            if (request.Headers["Referer"] is { Length: > 0 } referer)
                return $"a browser page sent this (Referer: {referer}). This API is for test scripts, not for pages.";

            string host = request.Headers["Host"] ?? string.Empty;
            return AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase)
                ? null
                : $"the request was addressed to '{host}', not to the loopback. Something resolved another name to this machine.";
        }

        sealed class AddAccountRequest
        {
            public string Email { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
            public IncomingProtocol Protocol { get; set; } = IncomingProtocol.Imap;
            public string IncomingHost { get; set; } = string.Empty;
            public int IncomingPort { get; set; }
            public ConnectionSecurity IncomingSecurity { get; set; } = ConnectionSecurity.Auto;
            public string SmtpHost { get; set; } = string.Empty;
            public int SmtpPort { get; set; }
            public ConnectionSecurity SmtpSecurity { get; set; } = ConnectionSecurity.Auto;
            public string Username { get; set; } = string.Empty;
            public bool TorOnly { get; set; }
        }

        sealed class SendRequest
        {
            public string AccountId { get; set; } = string.Empty;
            public string To { get; set; } = string.Empty;
            public string Cc { get; set; } = string.Empty;
            public string Subject { get; set; } = string.Empty;
            public string Body { get; set; } = string.Empty;
            public List<string> AttachmentPaths { get; set; } = [];
            /// <summary>True = go through OutboxService (undo window) exactly like the compose pane's Send button.</summary>
            public bool Queued { get; set; }
        }

        /// <summary>Returns the query parameter's value; a missing parameter throws "&lt;key&gt; is required.".</summary>
        static string RequireQueryValue(NameValueCollection query, string key) =>
            query[key] ?? throw new InvalidOperationException($"{key} is required.");

        /// <summary>Parses the mandatory "uid" query parameter.</summary>
        static uint RequireUid(NameValueCollection query) => uint.Parse(RequireQueryValue(query, "uid"));

        /// <summary>Looks the account up in <see cref="AccountStore"/>; throws when the id is unknown.</summary>
        static MailAccountData RequireAccount(string accountId) =>
            AccountStore.GetById(accountId) ?? throw new InvalidOperationException("Unknown account.");

        /// <summary>Looks the cached summary up in <see cref="MessageStore"/>; throws when the message is unknown.</summary>
        static MailMessageSummary RequireSummary(string accountId, string folderFullName, uint uid) =>
            MessageStore.GetSummary(accountId, folderFullName, uid) ?? throw new InvalidOperationException("Unknown message.");

        /// <summary>Reads the optional "take" query parameter, falling back to the endpoint's default.</summary>
        static int ReadTake(NameValueCollection query, int fallback) =>
            int.TryParse(query["take"], out int parsed) ? parsed : fallback;

        /// <summary>Reads the optional "folder" query parameter, defaulting to INBOX.</summary>
        static string ReadFolder(NameValueCollection query) => query["folder"] ?? MessageStore.InboxFullName;

        static async Task<object?> RouteAsync(HttpListenerRequest request)
        {
            string path = request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? string.Empty;
            var query = HttpUtility.ParseQueryString(request.Url?.Query ?? string.Empty);
            return path switch
            {
                "/status" or "/select-folder" or "/notify/probe" or "/downloads/probe" or "/link/open" or "/dom" or "/exit" or "/run-lock" or "/navigate" or "/dom/script" or "/dom/click" or "/dom/mouse" or "/mouse" or "/timings" or "/timings/clear" or "/logs" => await HandleApplicationAsync(request, query),
                "/reader-scale" or "/motion" or "/settings" or "/settings/set" or "/settings/reset" or "/start-folder" or "/accounts" or "/accounts/order" or "/accounts/enabled" or "/accounts/tor-only" => await HandleSettingsAsync(request, query),
                "/sync/folders" or "/connect-diagnose" or "/sync" or "/idle" or "/tls-choice" or "/tor" or "/tor/check" or "/tor/start" or "/tor/stop" or "/tor/connect-test" or "/health" or "/health/clear" => await HandleConnectionsAsync(request, query),
                "/folders/server" or "/folders/roles" or "/folders/roles/probe" or "/folder-why" or "/folder-why/act" or "/folder-tree" or "/folders" or "/folder/read-all" or "/backfill" or "/resync" or "/export/folder" => await HandleFoldersAsync(request, query),
                "/trim" or "/store/check" or "/trim-rehearsal" or "/export" => await HandleStorageAsync(request, query),
                "/compose/from" or "/reply-preview" or "/signature" or "/compose/preview" or "/forward-attach" or "/send" or "/flush-outbox" or "/send-check" or "/outbox" or "/outbox/hold/clear" or "/outbox/retry" or "/undo" or "/compose" => await HandleComposeAsync(request, query),
                "/auth" or "/search-rescue" or "/threads/probe" or "/person" or "/person/open" or "/message" or "/select" or "/tag" or "/read" or "/messages" or "/threads" or "/open" or "/source" or "/list" or "/identity" or "/unsubscribe" or "/contacts" or "/move" => await HandleMessagesAsync(request, query),
                "/rule-audit" or "/rule-audit/probe" or "/rule-preview" or "/mute" or "/muted" or "/mute/incoming-probe" or "/snooze" or "/snooze/wake" or "/snoozed" => HandleRules(request, query),
                _ => throw new InvalidOperationException($"Unknown endpoint: {request.HttpMethod} {path}")
            };
        }
    }
}
