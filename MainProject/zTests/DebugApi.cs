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
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.Constants;
using GlobalSettings = MyLovelyMail.MainProject.Stores.Settings;
using MyLovelyMail.MainProject.Constants.ThemeConstants;

namespace MyLovelyMail.MainProject.ZTests
{
    /// <summary>
    /// DEBUG-ONLY localhost REST API so the running app can be driven and inspected from outside
    /// (automated end-to-end tests against a local mail server). Never started in Release builds.
    /// Strictly a consumer of the existing services — it owns no mail logic of its own.
    /// Base address: <c>http://127.0.0.1:52539/</c>.
    /// <para>
    /// The routes are the <c>case ("VERB", "/path")</c> labels of <see cref="RouteAsync"/> and are
    /// deliberately NOT listed here: the list that used to live in this comment named eighteen
    /// while the switch had grown past forty, so it read as documentation and worked as
    /// misinformation. Read the switch — every case carries the reason it exists.
    /// </para>
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
        static string ReadFolder(NameValueCollection query) => query["folder"] ?? MessageStore.InboxFullName;

        static async Task<object?> RouteAsync(HttpListenerRequest request)
        {
            string path = request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? string.Empty;
            var query = HttpUtility.ParseQueryString(request.Url?.Query ?? string.Empty);

            switch (request.HttpMethod, path)
            {
                case ("GET", "/status"):
                    return new
                    {
                        app = AppConstants.AppName,
                        vaultUnlocked = CredentialVault.IsUnlocked,
                        syncing = SyncScheduler.IsSyncing,
                        userDataRoot = AppPaths.Root,
                        selectedAccountId = MailUiState.SelectedAccount?.Id,
                        selectedFolder = MailUiState.SelectedFolder?.FullName,
                        openMessageUid = MailUiState.OpenMessage?.Uid,
                        rememberedFolder = MailUiState.SelectedAccount is { } open ? FolderMemoryStore.FolderOf(open.Id) : null,
                        rememberedAccountId = FolderMemoryStore.LastAccountId,
                        accounts = AccountStore.Accounts.Select(a => new { a.Id, a.EmailAddress, a.Protocol, a.IncomingHost, a.Enabled, a.SortOrder })
                    };

                // Drives the same selection path the sidebar uses, so the folder memory can be
                // exercised without touching the screen.
                case ("POST", "/select-folder"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string wanted = RequireQueryValue(query, "folder");
                    var folder = MessageStore.GetFolders(accountId).FirstOrDefault(f => f.FullName == wanted);
                    if (folder != null) MailUiState.SelectFolder(folder);
                    return new { selected = folder != null, folder = folder?.FullName, requested = wanted };
                }

                // ?dry=true measures without deleting, which is the only safe way to check the
                // budget stage against a real cache. GET returns the persisted receipt instead.
                case ("POST", "/trim"):
                    return OfflineCacheTrimmer.TrimAll(query["dry"] == "true");

                case ("GET", "/trim"):
                    return new { lastReport = OfflineCacheTrimmer.LastReport };

                // Answers "is this element really in the page" for anything a screenshot cannot
                // show: below the fold, inside a virtualized list, or a style that only emits CSS.
                case ("GET", "/dom"):
                {
                    string? html = await RenderedPageProbe.ReadHtmlAsync(query["selector"] ?? string.Empty);
                    return new { available = html != null, length = html?.Length ?? 0, html };
                }

                // Opens a page without touching the screen, so any page can be snapshot-audited.
                case ("POST", "/navigate"):
                {
                    string route = RequireQueryValue(query, "route");
                    return new { navigated = NavigationBridge.TryNavigate(route), route };
                }

                // Read-only by construction: Preview never runs the rule's actions, so this cannot
                // tag, move or mark-read anything in the user's cache.
                // Raw MIME on the body: every finding is provable by piping a handcrafted header
                // block, with no mail server and no waiting for a message that happens to be wrong.
                case ("POST", "/auth"):
                {
                    var message = await MimeMessage.LoadAsync(request.InputStream);
                    return new { findings = MessageAuthService.Inspect(message) };
                }

                case ("GET", "/auth"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    return new { findings = MessageAuthService.Read(RequireAccount(accountId), folder, RequireSummary(accountId, folder, uid)) };
                }

                case ("GET", "/export/folder"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    return MailboxExportService.ExportFolder(RequireAccount(accountId), ReadFolder(query));
                }

                case ("GET", "/search-rescue"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    return new { suggestions = SearchRescueService.Suggest(accountId, ReadFolder(query), RequireQueryValue(query, "query"), query["all"] == "true"), dead = SearchService.DeadOperators(RequireQueryValue(query, "query")) };
                }

                // One rotation step on its own, so a test can watch which folder it picks.
                case ("POST", "/sync/folders"):
                {
                    var account = RequireAccount(RequireQueryValue(query, "accountId"));
                    var before = MessageStore.GetFolders(account.Id).ToDictionary(f => f.FullName, f => f.LastSyncedUtc);
                    await ImapSyncService.SyncAccountAsync(account);
                    return new { refreshed = MessageStore.GetFolders(account.Id)
                        .Where(f => !f.IsLocal && f.LastSyncedUtc != null && before.GetValueOrDefault(f.FullName) != f.LastSyncedUtc)
                        .Select(f => new { f.FullName, f.LastSyncedUtc, CachedCount = MessageStore.GetSummaries(account.Id, f.FullName).Count }) };
                }

                // The quote is a pure function of the source message plus the style, so each
                // mode is checkable without opening a compose pane.
                case ("POST", "/compose/from"):
                {
                    var next = RequireAccount(RequireQueryValue(query, "accountId"));
                    if (MailUiState.ActiveCompose is not { } draft) throw new InvalidOperationException("No draft is open.");
                    ComposeService.SwitchSendingAccount(draft, next);
                    return new { from = draft.Account?.EmailAddress, bodyPrefix = draft.Body[..Math.Min(60, draft.Body.Length)], attachments = draft.AttachmentPaths.Count(File.Exists) };
                }

                case ("GET", "/reply-preview"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    var style = Enum.Parse<QuoteStyle>(query["style"] ?? nameof(QuoteStyle.Full), ignoreCase: true);
                    string body = ComposeService.QuoteBody(RequireAccount(accountId), folder, RequireSummary(accountId, folder, uid), style);
                    return new { style = style.ToString(), lines = body.Split('\n').Length, characters = body.Length, body };
                }

                // ?probe= runs the builder over handmade paths, so the nesting rules are checkable
                // without a server that happens to have nested folders.
                case ("GET", "/rule-audit"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    var summary = MessageStore.GetSummary(accountId, folder, uid);
                    return new { entries = RuleAuditStore.For(accountId, folder, uid, summary?.MessageId ?? string.Empty) };
                }

                // Feeds a synthetic arrival straight to the rule engine, so the trace is checkable
                // without waiting for mail that happens to match a rule.
                case ("POST", "/rule-audit/probe"):
                {
                    var account = RequireAccount(RequireQueryValue(query, "accountId"));
                    var probe = new MailMessageSummary
                    {
                        Uid = uint.MaxValue,
                        MessageId = query["messageId"] ?? "<rule-probe@mylovelymail.invalid>",
                        FromAddress = query["from"] ?? "probe@example.invalid",
                        Subject = query["subject"] ?? string.Empty,
                        DateUtc = DateTime.UtcNow
                    };
                    RuleEngine.ProcessIncoming(account, ReadFolder(query), [probe]);
                    return new { flags = probe.Flags.ToString(), probe.Tags, entries = RuleAuditStore.For(account.Id, ReadFolder(query), probe.Uid, probe.MessageId) };
                }

                case ("GET", "/person"):
                    return PersonProfileService.Build(RequireQueryValue(query, "accountId"), RequireQueryValue(query, "address"));

                case ("POST", "/person/open"):
                {
                    string address = RequireQueryValue(query, "address");
                    if (address == "close") MailUiState.ClosePerson(); else MailUiState.OpenPerson(address);
                    return new { open = MailUiState.OpenPersonAddress };
                }

                case ("GET", "/folder-tree"):
                {
                    List<MailFolderData> folders = query["probe"] is { Length: > 0 } probe
                        ? [.. probe.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(name => new MailFolderData { FullName = name, DisplayName = name.Split('/').Last(), Delimiter = '/', IsLocal = name.StartsWith(MessageStore.LocalFolderPrefix) })]
                        : MessageStore.GetFolders(RequireQueryValue(query, "accountId"));
                    return FolderTreeService.Build(folders).Select(n => new { n.Folder.FullName, n.Folder.DisplayName, n.Depth });
                }

                case ("GET", "/rule-preview"):
                {
                    string ruleId = RequireQueryValue(query, "ruleId");
                    var rule = FilterRuleStore.Rules.FirstOrDefault(r => r.Id == ruleId)
                        ?? throw new InvalidOperationException("Unknown rule.");
                    return RuleEngine.Preview(rule);
                }

                case ("POST", "/reader-scale"):
                {
                    GlobalSettings.ReaderTextScalePercent.Value = int.Parse(RequireQueryValue(query, "percent"));
                    SettingsManager.SaveSettings();
                    return new { GlobalSettings.ReaderTextScalePercent.Value };
                }

                case ("GET", "/motion"):
                    return new
                    {
                        reduceMotion = GlobalSettings.ReduceMotion.Value,
                        followSystem = GlobalSettings.FollowSystemMotion.Value,
                        systemReduced = MotionPreference.SystemPrefersReduced,
                        effective = MotionPreference.IsCalm,
                        css = AppStyles.BuildCalmMotionLayer()
                    };

                // ?reduce= sets the app switch and stops following the system, since the switch
                // means nothing while the OS is the source of the answer; ?follow= sets that
                // choice on its own.
                case ("POST", "/motion"):
                {
                    if (query["follow"] is { Length: > 0 } follow)
                        GlobalSettings.FollowSystemMotion.Value = follow == "true";
                    if (query["reduce"] is { Length: > 0 } reduce)
                    {
                        GlobalSettings.FollowSystemMotion.Value = false;
                        GlobalSettings.ReduceMotion.Value = reduce == "true";
                    }
                    SettingsManager.SaveSettings();
                    return new { GlobalSettings.ReduceMotion.Value, followSystem = GlobalSettings.FollowSystemMotion.Value, effective = MotionPreference.IsCalm, css = AppStyles.BuildCalmMotionLayer() };
                }

                // Rehearses the size stage against a real cache by lending one account a budget for
                // the duration of a dry run, then putting its setting back exactly as it was.
                case ("POST", "/trim-rehearsal"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    int budgetMb = int.Parse(RequireQueryValue(query, "mb"));
                    var budget = AccountStore.GetSettings(accountId).OfflineMaxCacheMb;
                    bool wasOverridden = budget.IsOverridden;
                    int previous = budget.Value;
                    budget.Value = budgetMb;
                    try
                    {
                        return OfflineCacheTrimmer.TrimAll(dryRun: true);
                    }
                    finally
                    {
                        if (wasOverridden) budget.Value = previous; else budget.ClearOverride();
                    }
                }

                // Answers "where would this account open right now" without selecting anything, so
                // the restore — including the phantom-folder fallback — is checkable while the app
                // is in use. ?pretend= substitutes a remembered name the server does not have.
                case ("GET", "/start-folder"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    var folders = MessageStore.GetFolders(accountId);
                    string? remembered = FolderMemoryStore.FolderOf(accountId);
                    string? pretend = query["pretend"];
                    var resolved = FolderMemoryStore.ResolveStartFolder(pretend is { Length: > 0 } ? pretend : remembered, folders);
                    return new { remembered, pretended = pretend, opensIn = resolved?.FullName };
                }

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
                        .Select(f => new
                        {
                            f.FullName, f.DisplayName, f.Role, f.TotalCount, f.UnreadCount, f.IsLocal,
                            f.LastSeenUid, f.OldestFetchedUid, f.LastSyncedUtc, f.Delimiter,
                            CachedCount = MessageStore.GetSummaries(accountId, f.FullName).Count
                        });
                }

                case ("POST", "/accounts/order"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    AccountStore.Move(accountId, int.Parse(RequireQueryValue(query, "delta")));
                    return new { order = AccountStore.Accounts.Select(a => new { a.EmailAddress, a.SortOrder }) };
                }

                // Flips the account's Enabled flag through the same store path the settings page
                // uses, so the paused rendering can be checked without opening that page.
                case ("POST", "/accounts/enabled"):
                {
                    var account = RequireAccount(RequireQueryValue(query, "accountId"));
                    account.Enabled = RequireQueryValue(query, "enabled") == "true";
                    AccountStore.Save(account);
                    return new { account.EmailAddress, account.Enabled };
                }

                case ("POST", "/mute"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    var account = RequireAccount(accountId);
                    var summary = RequireSummary(accountId, folder, uid);
                    int touched = query["mute"] == "false"
                        ? MuteService.UnmuteThread(account, folder, summary)
                        : MuteService.MuteThread(account, folder, summary);
                    return new { touched, mutedThreadIds = MutedThreadStore.All.Count };
                }

                case ("GET", "/muted"):
                    return new { messageIds = MutedThreadStore.All };

                // Hands ApplyToIncoming a summary that only claims to be a reply, which is the one
                // way to prove the future-arrival half without waiting on real mail. A resync
                // proves nothing here: Muted survives that through CarryOverLocalState anyway.
                case ("POST", "/mute/incoming-probe"):
                {
                    var probe = new MailMessageSummary
                    {
                        Uid = uint.MaxValue,
                        MessageId = query["messageId"] ?? "<probe@mylovelymail.invalid>",
                        InReplyTo = RequireQueryValue(query, "inReplyTo")
                    };
                    bool muted = MuteService.ApplyToIncoming(probe);
                    return new { muted, flags = probe.Flags.ToString(), remembered = MutedThreadStore.All.Contains(probe.MessageId) };
                }

                // Goes through MessageActions.Delete, so it honours DeleteAction the same way the
                // list does: a server folder moves to Trash, a local folder is removed outright.
                case ("DELETE", "/message"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    MessageActions.Delete(RequireAccount(accountId), folder, RequireSummary(accountId, folder, uid));
                    return new { deleted = uid, folder };
                }

                case ("POST", "/read"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    bool read = query["read"] != "false";
                    MessageActions.SetRead(RequireAccount(accountId), folder, RequireSummary(accountId, folder, uid), read);
                    return new { uid, read };
                }

                case ("POST", "/folder/read-all"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    return new { marked = MessageActions.SetFolderRead(RequireAccount(accountId), folder) };
                }

                // Awaited, unlike the UI's fire-and-forget kick, so a test can read the count back.
                case ("POST", "/backfill"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    int landed = await ImapSyncService.BackfillFolderAsync(RequireAccount(accountId), folder, ReadTake(query, 300));
                    return new { landed };
                }

                case ("GET", "/messages"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    return MessageStore.GetSummaries(accountId, folder).Take(ReadTake(query, 20))
                        .Select(s => new { s.Uid, s.Subject, s.FromAddress, s.DateUtc, s.Flags, s.HasAttachments, s.PreviewText });
                }

                case ("GET", "/message"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    var account = RequireAccount(accountId);
                    var summary = RequireSummary(accountId, folder, uid);

                    async Task DownloadBodyAsync()
                    {
                        if (account.Protocol == IncomingProtocol.Imap)
                            await ImapSyncService.DownloadMessageAsync(account, folder, uid);
                        else
                            await Pop3Service.DownloadMessageAsync(account, uid);
                    }

                    if (!MessageStore.HasFullMessage(accountId, folder, uid))
                        await DownloadBodyAsync();

                    // A render that comes back null means the cached body was unreadable and has
                    // just been dropped — the reader downloads again at that point, so this does
                    // too, or the corrupt case would need two calls to answer.
                    var rendered = MailBodyRenderer.Render(account, folder, summary);
                    if (rendered == null)
                    {
                        await DownloadBodyAsync();
                        rendered = MailBodyRenderer.Render(account, folder, summary);
                    }

                    return new
                    {
                        summary.Uid,
                        summary.Subject,
                        summary.FromAddress,
                        summary.ToAddresses,
                        bodyHtml = rendered?.Html,
                        attachments = AttachmentService.List(account, folder, summary)
                    };
                }

                case ("GET", "/threads"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
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
                    string folderFullName = ReadFolder(query);
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

                case ("GET", "/source"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    var mime = MessageStore.TryLoadMimeMessage(accountId, folder, uid);
                    return new
                    {
                        cached = mime != null,
                        headers = mime?.Headers.Select(h => new { name = h.Field, value = h.Value })
                    };
                }

                case ("GET", "/list"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    string searchQuery = query["query"] ?? string.Empty;
                    // Always through the matcher, exactly like the list page: an empty query is not
                    // "no filtering" — it is still what hides snoozed mail.
                    var matcher = SearchService.BuildMatcher(searchQuery);
                    return MessageStore.GetSummaries(accountId, folder)
                        .Where(matcher)
                        .Take(ReadTake(query, 20))
                        .Select(s => new { s.Uid, s.Subject, s.FromAddress, unread = s.IsUnread, s.HasAttachments });
                }

                case ("POST", "/resync"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    ImapSyncService.KickFolderResync(RequireAccount(accountId), folder);
                    return new { ok = true };
                }

                case ("POST", "/snooze"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    int minutes = int.TryParse(query["minutes"], out int parsedMinutes) ? parsedMinutes : 60;
                    SnoozeService.Snooze(RequireAccount(accountId), folder, RequireSummary(accountId, folder, uid),
                        DateTime.UtcNow.AddMinutes(minutes));
                    return new { ok = true, dueUtc = DateTime.UtcNow.AddMinutes(minutes) };
                }

                case ("POST", "/snooze/wake"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    SnoozeService.Wake(RequireAccount(accountId), folder, RequireSummary(accountId, folder, uid));
                    return new { ok = true };
                }

                case ("GET", "/snoozed"):
                    return SnoozeService.Snoozed(RequireQueryValue(query, "accountId"))
                        .Select(entry => new { folder = entry.FolderFullName, entry.Summary.Uid, entry.Summary.Subject, entry.Summary.SnoozedUntilUtc });

                case ("GET", "/identity"):
                {
                    var colors = AppColors.IdentityPalette.Concat(AccountStore.Accounts.Select(a => a.ColorHex)).Distinct();
                    return colors.Select(hex =>
                    {
                        var ink = AppColors.InkOn(hex);
                        return new
                        {
                            background = hex,
                            ink = ink.ToRgbaHex(true),
                            ratio = Math.Round(AppColors.ContrastRatio(Color.FromArgb(hex), ink), 2)
                        };
                    });
                }

                case ("GET", "/health"):
                    return AccountStore.Accounts.Select(a => new
                    {
                        a.Id,
                        a.EmailAddress,
                        health = SyncHealthService.For(a.Id)
                    });

                // A raw header can be passed instead of a message, so the parser and the link gate
                // can be exercised on the shapes real newsletters send without hunting for one.
                // Builds a draft from the query and runs the pre-send checks on it, so every
                // warning is reachable without a compose window.
                case ("GET", "/send-check"):
                {
                    var draft = new ComposeDraft
                    {
                        Account = query["accountId"] is { Length: > 0 } guardAccountId ? RequireAccount(guardAccountId) : null,
                        To = query["to"] ?? string.Empty,
                        Cc = query["cc"] ?? string.Empty,
                        Subject = query["subject"] ?? string.Empty,
                        Body = query["body"] ?? string.Empty,
                        ArrivedAtAddress = query["arrivedAt"] ?? string.Empty
                    };
                    // GetValues, not the indexer: a multi-file draft is what the size ceiling needs.
                    draft.AttachmentPaths.AddRange(query.GetValues("attachment") ?? []);
                    return new { warnings = SendGuardService.Inspect(draft) };
                }

                case ("GET", "/unsubscribe") when query["header"] is { Length: > 0 } rawHeader:
                {
                    var parsed = UnsubscribeService.Parse(rawHeader);
                    return new { found = parsed != null, parsed?.HttpUrl, parsed?.MailtoAddress, parsed?.MailtoSubject };
                }

                case ("GET", "/unsubscribe"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    uint uid = RequireUid(query);
                    var targets = UnsubscribeService.Read(RequireAccount(accountId), folder, RequireSummary(accountId, folder, uid));
                    return new { found = targets != null, targets?.HttpUrl, targets?.MailtoAddress, targets?.MailtoSubject };
                }

                case ("GET", "/contacts"):
                    return ContactIndexService
                        .Suggest(RequireQueryValue(query, "accountId"), query["prefix"] ?? string.Empty, ReadTake(query, 20))
                        .Select(c => new { c.Address, c.DisplayName, c.Suggestion });

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
                    string folder = ReadFolder(query);
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
                    string folder = ReadFolder(query);
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
