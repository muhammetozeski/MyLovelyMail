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
using MyLovelyMail.MainProject.Services.Tor;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.Constants;
using GlobalSettings = MyLovelyMail.MainProject.Stores.Settings;
using MyLovelyMail.MainProject.Constants.ThemeConstants;

namespace MyLovelyMail.MainProject.Diagnostics;

public static partial class DebugApi
{
    /// <summary>Handles the messages endpoints using the application's existing services.</summary>
    /// <param name="request">The accepted local request, including its method and body.</param>
    /// <param name="query">Decoded query values shared with the route dispatcher.</param>
    /// <returns>The response object serialized by the listener.</returns>
    static async Task<object?> HandleMessagesAsync(HttpListenerRequest request, NameValueCollection query)
    {
        switch (request.HttpMethod, request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant())
        {

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

                case ("GET", "/search-rescue"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    return new { suggestions = SearchRescueService.Suggest(accountId, ReadFolder(query), RequireQueryValue(query, "query"), query["all"] == "true"), dead = SearchService.DeadOperators(RequireQueryValue(query, "query")) };
                }

                // Synthetic summaries straight into the grouper: a folder of identical subjects
                // is the case that breaks it, and waiting for one to exist is not a test.
                case ("POST", "/threads/probe"):
                {
                    int count = ReadTake(query, 400);
                    int hoursApart = int.TryParse(query["hoursApart"], out int parsed) ? parsed : 24;
                    string subject = query["subject"] ?? "Re: Backup report";
                    var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    List<MailMessageSummary> probes = [.. Enumerable.Range(0, count).Select(i => new MailMessageSummary
                    {
                        Uid = (uint)(i + 1),
                        MessageId = $"<probe-{i}@mylovelymail.invalid>",
                        Subject = subject,
                        DateUtc = start.AddHours(i * hoursApart)
                    })];
                    var threads = ThreadingService.BuildThreads(probes);
                    return new
                    {
                        messages = count,
                        threads = threads.Count,
                        largest = threads.Max(t => t.Messages.Count),
                        spans = threads.Take(5).Select(t => new { size = t.Messages.Count, days = (int)(t.Newest.DateUtc - t.Messages[0].DateUtc).TotalDays })
                    };
                }

                case ("GET", "/person"):
                    return PersonProfileService.Build(RequireQueryValue(query, "accountId"), RequireQueryValue(query, "address"));

                case ("POST", "/person/open"):
                {
                    string address = RequireQueryValue(query, "address");
                    if (address == "close") MailUiState.ClosePerson(); else MailUiState.OpenPerson(address);
                    return new { open = MailUiState.OpenPersonAddress };
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

                // Ticks rows the way Ctrl+Click does, so the bulk bar - which only exists while
                // something is selected - can be audited at all.
                case ("POST", "/select"):
                {
                    var uids = (query["uids"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToList();
                    if (uids.Count == 0) MailUiState.ClearSelection(); else MailUiState.SelectMany(uids);
                    return new { selected = MailUiState.SelectedMessages.Count };
                }

                case ("POST", "/tag"):
                {
                    string accountId = RequireQueryValue(query, "accountId");
                    string folder = ReadFolder(query);
                    var uids = (query["uids"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToHashSet();
                    var targets = MessageStore.GetSummaries(accountId, folder).Where(s => uids.Contains(s.Uid));
                    int changed = MessageActions.SetTagOnMany(RequireAccount(accountId), folder, targets, RequireQueryValue(query, "tag"), query["remove"] != "true");
                    return new { changed };
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
                    // "no filtering" — it is still what hides snoozed mail. The account goes with
                    // it, because "to:" means mail THIS account sent.
                    var matcher = SearchService.BuildMatcher(searchQuery, accountId);
                    return MessageStore.GetSummaries(accountId, folder)
                        .Where(matcher)
                        .Take(ReadTake(query, 20))
                        .Select(s => new { s.Uid, s.Subject, s.FromAddress, unread = s.IsUnread, s.HasAttachments });
                }

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

            default:
                throw new InvalidOperationException($"Unknown endpoint: {request.HttpMethod} {request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant()}");
        }
    }
}
