using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.ZTests
{
    /// <summary>
    /// DEBUG-ONLY localhost REST API so the running app can be driven and inspected from outside
    /// (automated end-to-end tests against a local mail server). Never started in Release builds.
    /// Strictly a consumer of the existing services — it owns no mail logic of its own.
    /// Base address: http://127.0.0.1:52539/ — endpoints:
    ///   GET  /status                                  app + accounts overview
    ///   POST /accounts   {email,password,hosts,...}   add account into vault+store, kicks a sync
    ///   POST /sync                                    run a full sync pass now (awaited)
    ///   GET  /folders?accountId=                      cached folders of the account
    ///   GET  /messages?accountId=&amp;folder=&amp;take=      newest summaries of a folder
    ///   GET  /message?accountId=&amp;folder=&amp;uid=        rendered body + attachments of one message
    ///   POST /send       {accountId,to,cc,subject,body,attachmentPaths,queued}   send through SMTP (queued=true goes via OutboxService)
    ///   POST /open       ?accountId=&amp;folder=&amp;uid=     select + open the message in the reader
    ///   POST /compose    ?accountId=&amp;attachmentPath=   open the compose pane (optionally pre-attach a file)
    ///   GET  /threads    ?accountId=&amp;folder=&amp;take=     conversations built by ThreadingService
    ///   GET  /logs       ?filter=&amp;take=                in-memory log lines
    ///   GET  /export     ?accountId=&amp;folder=&amp;uid=&amp;format=   save the message as .eml (format=html for .html), returns the path
    ///   POST /move       ?accountId=&amp;folder=&amp;uid=&amp;target=   move the message to another folder
    ///   POST /flush-outbox                             send everything waiting in the outbox now
    ///   POST /undo                                     cancel the queued send and reopen the draft
    ///   DELETE /accounts ?accountId=                   remove the account
    /// </summary>
    public static class DebugApi
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
            try
            {
                result = await RouteAsync(context.Request);
            }
            catch (Exception ex)
            {
                status = 500;
                result = new { error = ex.Message };
            }

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(result, Json);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentEncoding = Encoding.UTF8;
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
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
        static string ReadFolder(NameValueCollection query) => query["folder"] ?? "INBOX";

        static async Task<object?> RouteAsync(HttpListenerRequest request)
        {
            string path = request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? string.Empty;
            var query = HttpUtility.ParseQueryString(request.Url?.Query ?? string.Empty);

            switch (request.HttpMethod, path)
            {
                case ("GET", "/status"):
                    return new
                    {
                        app = "MyLovelyMail",
                        vaultUnlocked = CredentialVault.IsUnlocked,
                        syncing = SyncScheduler.IsSyncing,
                        userDataRoot = AppPaths.Root,
                        accounts = AccountStore.Accounts.Select(a => new { a.Id, a.EmailAddress, a.Protocol, a.IncomingHost, a.Enabled })
                    };

                case ("POST", "/accounts"):
                {
                    var body = await JsonSerializer.DeserializeAsync<AddAccountRequest>(request.InputStream, Json)
                        ?? throw new InvalidOperationException("Empty request body.");
                    var account = new MailAccountData
                    {
                        EmailAddress = body.Email,
                        DisplayName = body.DisplayName.Length > 0 ? body.DisplayName : body.Email,
                        Protocol = body.Protocol,
                        IncomingHost = body.IncomingHost,
                        IncomingPort = body.IncomingPort,
                        IncomingSecurity = body.IncomingSecurity,
                        IncomingUsername = body.Username.Length > 0 ? body.Username : body.Email,
                        SmtpHost = body.SmtpHost,
                        SmtpPort = body.SmtpPort,
                        SmtpSecurity = body.SmtpSecurity
                    };
                    CredentialVault.SetPassword(account.Id, body.Password);
                    AccountStore.Save(account);
                    _ = SyncScheduler.SyncNowAsync();
                    return new { account.Id };
                }

                case ("POST", "/sync"):
                    await SyncScheduler.SyncNowAsync();
                    return new { ok = true, syncing = SyncScheduler.IsSyncing };

                case ("GET", "/folders"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    return MessageStore.GetFolders(accountId)
                        .Select(f => new { f.FullName, f.DisplayName, f.Role, f.TotalCount, f.UnreadCount, f.IsLocal });
                }

                case ("GET", "/messages"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = query["folder"] ?? "INBOX";
                    return MessageStore.GetSummaries(accountId, folder).Take(ReadTake(query, 20))
                        .Select(s => new { s.Uid, s.Subject, s.FromAddress, s.DateUtc, s.Flags, s.HasAttachments, s.PreviewText });
                }

                case ("GET", "/message"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = query["folder"] ?? "INBOX";
                    uint uid = RequireUid(query);
                    var account = RequireAccount(accountId);
                    var summary = RequireSummary(accountId, folder, uid);

                    if (!MessageStore.HasFullMessage(accountId, folder, uid))
                    {
                        if (account.Protocol == IncomingProtocol.Imap)
                            await ImapSyncService.DownloadMessageAsync(account, folder, uid);
                        else
                            await Pop3Service.DownloadMessageAsync(account, uid);
                    }

                    return new
                    {
                        summary.Uid,
                        summary.Subject,
                        summary.FromAddress,
                        summary.ToAddresses,
                        bodyHtml = MailBodyRenderer.Render(account, folder, summary)?.Html,
                        attachments = AttachmentService.List(account, folder, summary)
                    };
                }

                case ("GET", "/threads"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = query["folder"] ?? "INBOX";
                    return ThreadingService.BuildThreads(MessageStore.GetSummaries(accountId, folder)).Take(ReadTake(query, 10))
                        .Select(t => new
                        {
                            subject = t.Newest.Subject,
                            count = t.Messages.Count,
                            unread = t.UnreadCount,
                            participants = t.ParticipantAddresses,
                            uids = t.Messages.Select(m => m.Uid)
                        });
                }

                case ("GET", "/logs"):
                {
                    string? filter = query["filter"];
                    IEnumerable<string> lines = Logger.AllLogs;
                    if (!string.IsNullOrEmpty(filter))
                        lines = lines.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase));
                    return lines.TakeLast(ReadTake(query, 100)).ToArray();
                }

                case ("POST", "/open"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folderFullName = query["folder"] ?? "INBOX";
                    uint uid = RequireUid(query);
                    var account = RequireAccount(accountId);
                    var folder = MessageStore.GetFolders(accountId).FirstOrDefault(f => f.FullName == folderFullName)
                        ?? throw new InvalidOperationException("Unknown folder.");
                    var summary = RequireSummary(accountId, folderFullName, uid);

                    MailUiState.SelectAccount(account);
                    MailUiState.SelectFolder(folder);
                    MailUiState.OpenMessageInReader(summary);
                    return new { ok = true };
                }

                case ("POST", "/send"):
                {
                    var body = await JsonSerializer.DeserializeAsync<SendRequest>(request.InputStream, Json)
                        ?? throw new InvalidOperationException("Empty request body.");
                    var account = RequireAccount(body.AccountId);
                    var draft = new ComposeDraft
                    {
                        Account = account,
                        To = body.To,
                        Cc = body.Cc,
                        Subject = body.Subject,
                        Body = body.Body
                    };
                    foreach (string sourcePath in body.AttachmentPaths)
                    {
                        await using var source = File.OpenRead(sourcePath);
                        await ComposeService.AttachFileAsync(draft, source, Path.GetFileName(sourcePath));
                    }
                    if (body.Queued)
                    {
                        ComposeService.SaveDraft(draft);
                        OutboxService.Enqueue(draft);
                        return new { ok = true, queued = true, dueUtc = OutboxService.Current?.DueUtc };
                    }
                    var outcome = await ComposeService.SendAsync(draft);
                    return new { ok = true, outcome = outcome.ToString(), attachments = draft.AttachmentPaths.Count };
                }

                case ("DELETE", "/accounts"):
                {
                    AccountStore.Remove(RequireQueryValue(query, "accountId"));
                    return new { ok = true };
                }

                case ("POST", "/flush-outbox"):
                    await OutboxService.FlushAsync();
                    return new { ok = true };

                case ("GET", "/outbox"):
                    return new
                    {
                        pending = OutboxService.Current is { } pending ? new { pending.Draft.Subject, pending.DueUtc } : null,
                        failure = OutboxService.LastFailure is { } failure
                            ? new { failure.Draft.Subject, failure.Reason, failure.FailedAtUtc }
                            : null
                    };

                case ("POST", "/outbox/retry"):
                    OutboxService.RetryFailed();
                    return new { ok = true };

                case ("POST", "/undo"):
                {
                    var draft = OutboxService.Undo();
                    if (draft != null) MailUiState.OpenCompose(draft);
                    return new { undone = draft != null };
                }

                case ("GET", "/export"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = query["folder"] ?? "INBOX";
                    uint uid = RequireUid(query);
                    var account = RequireAccount(accountId);
                    var summary = RequireSummary(accountId, folder, uid);
                    if (!MessageStore.HasFullMessage(accountId, folder, uid))
                        await ImapSyncService.DownloadMessageAsync(account, folder, uid);
                    string exportPath = query["format"] == "html"
                        ? MessageExportService.ExportHtml(account, folder, summary)
                        : MessageExportService.ExportEml(account, folder, summary);
                    return new { path = exportPath };
                }

                case ("POST", "/move"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = query["folder"] ?? "INBOX";
                    string target = RequireQueryValue(query, "target");
                    uint uid = RequireUid(query);
                    var account = RequireAccount(accountId);
                    var summary = RequireSummary(accountId, folder, uid);
                    // Mirrors the bulk bar's branch: a "Local/x" target files locally, anything else moves on the server.
                    if (target.StartsWith(MessageStore.LocalFolderPrefix))
                        MessageActions.MoveToLocalFolder(account, folder, [summary], target[MessageStore.LocalFolderPrefix.Length..]);
                    else
                        MessageActions.MoveToFolder(account, folder, [summary], target);
                    return new { ok = true };
                }

                case ("POST", "/compose"):
                {
                    var account = RequireAccount(RequireQueryValue(query, "accountId"));
                    var draft = ComposeService.BuildNew(account);
                    if (query["attachmentPath"] is { Length: > 0 } attachmentPath)
                    {
                        await using var source = File.OpenRead(attachmentPath);
                        await ComposeService.AttachFileAsync(draft, source, Path.GetFileName(attachmentPath));
                    }
                    MailUiState.SelectAccount(account);
                    MailUiState.OpenCompose(draft);
                    return new { ok = true, draft.DraftId, attachments = draft.AttachmentPaths.Count };
                }

                default:
                    throw new InvalidOperationException($"Unknown endpoint: {request.HttpMethod} {path}");
            }
        }
    }
}
