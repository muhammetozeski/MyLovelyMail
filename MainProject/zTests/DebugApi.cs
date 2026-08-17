using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    ///   POST /send       {accountId,to,cc,subject,body,attachmentPaths}   send through SMTP
    ///   POST /open       ?accountId=&amp;folder=&amp;uid=     select + open the message in the reader
    ///   POST /compose    ?accountId=&amp;attachmentPath=   open the compose pane (optionally pre-attach a file)
    ///   GET  /threads    ?accountId=&amp;folder=&amp;take=     conversations built by ThreadingService
    ///   GET  /logs       ?filter=&amp;take=                in-memory log lines
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
        }

        static async Task<object?> RouteAsync(HttpListenerRequest request)
        {
            string path = request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? string.Empty;
            var query = System.Web.HttpUtility.ParseQueryString(request.Url?.Query ?? string.Empty);

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
                    string accountId = query["accountId"] ?? throw new InvalidOperationException("accountId is required.");
                    return MessageStore.GetFolders(accountId)
                        .Select(f => new { f.FullName, f.DisplayName, f.Role, f.TotalCount, f.UnreadCount, f.IsLocal });
                }

                case ("GET", "/messages"):
                {
                    string accountId = query["accountId"] ?? throw new InvalidOperationException("accountId is required.");
                    string folder = query["folder"] ?? "INBOX";
                    int take = int.TryParse(query["take"], out int parsed) ? parsed : 20;
                    return MessageStore.GetSummaries(accountId, folder).Take(take)
                        .Select(s => new { s.Uid, s.Subject, s.FromAddress, s.DateUtc, s.Flags, s.HasAttachments, s.PreviewText });
                }

                case ("GET", "/message"):
                {
                    string accountId = query["accountId"] ?? throw new InvalidOperationException("accountId is required.");
                    string folder = query["folder"] ?? "INBOX";
                    uint uid = uint.Parse(query["uid"] ?? throw new InvalidOperationException("uid is required."));
                    var account = AccountStore.GetById(accountId) ?? throw new InvalidOperationException("Unknown account.");
                    var summary = MessageStore.GetSummary(accountId, folder, uid) ?? throw new InvalidOperationException("Unknown message.");

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
                    string accountId = query["accountId"] ?? throw new InvalidOperationException("accountId is required.");
                    string folder = query["folder"] ?? "INBOX";
                    int take = int.TryParse(query["take"], out int parsed) ? parsed : 10;
                    return ThreadingService.BuildThreads(MessageStore.GetSummaries(accountId, folder)).Take(take)
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
                    int take = int.TryParse(query["take"], out int parsed) ? parsed : 100;
                    string? filter = query["filter"];
                    IEnumerable<string> lines = Logger.AllLogs;
                    if (!string.IsNullOrEmpty(filter))
                        lines = lines.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase));
                    return lines.TakeLast(take).ToArray();
                }

                case ("POST", "/open"):
                {
                    string accountId = query["accountId"] ?? throw new InvalidOperationException("accountId is required.");
                    string folderFullName = query["folder"] ?? "INBOX";
                    uint uid = uint.Parse(query["uid"] ?? throw new InvalidOperationException("uid is required."));
                    var account = AccountStore.GetById(accountId) ?? throw new InvalidOperationException("Unknown account.");
                    var folder = MessageStore.GetFolders(accountId).FirstOrDefault(f => f.FullName == folderFullName)
                        ?? throw new InvalidOperationException("Unknown folder.");
                    var summary = MessageStore.GetSummary(accountId, folderFullName, uid)
                        ?? throw new InvalidOperationException("Unknown message.");

                    MailUiState.SelectAccount(account);
                    MailUiState.SelectFolder(folder);
                    MailUiState.OpenMessageInReader(summary);
                    return new { ok = true };
                }

                case ("POST", "/send"):
                {
                    var body = await JsonSerializer.DeserializeAsync<SendRequest>(request.InputStream, Json)
                        ?? throw new InvalidOperationException("Empty request body.");
                    var account = AccountStore.GetById(body.AccountId) ?? throw new InvalidOperationException("Unknown account.");
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
                    await ComposeService.SendAsync(draft);
                    return new { ok = true, attachments = draft.AttachmentPaths.Count };
                }

                case ("POST", "/compose"):
                {
                    string accountId = query["accountId"] ?? throw new InvalidOperationException("accountId is required.");
                    var account = AccountStore.GetById(accountId) ?? throw new InvalidOperationException("Unknown account.");
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
