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

namespace MyLovelyMail.MainProject.ZTests;

public static partial class DebugApi
{
    /// <summary>Handles the application endpoints using the application's existing services.</summary>
    /// <param name="request">The accepted local request, including its method and body.</param>
    /// <param name="query">Decoded query values shared with the route dispatcher.</param>
    /// <returns>The response object serialized by the listener.</returns>
    static async Task<object?> HandleApplicationAsync(HttpListenerRequest request, NameValueCollection query)
    {
        switch (request.HttpMethod, request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant())
        {
                case ("GET", "/status"):
                    return new
                    {
                        app = AppConstants.AppName,
                        // Read from the running assembly, so this answers "which build is this"
                        // for a test the way the settings footer answers it for a person.
                        version = AppConstants.AppVersion,
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

                // Shows a made-up new-mail notification through the real presenter and sound, so the
                // notification path can be checked on a device whose accounts have no fresh mail.
                // Give accountId, folder and uid of a cached message to test what a tap opens.
                case ("POST", "/notify/probe"):
                {
                    var toast = new MailToast
                    {
                        Title = query["title"] ?? "Probe sender",
                        Body = query["body"] ?? "Probe subject",
                        SoundName = query["sound"] ?? "default",
                        AccountId = query["accountId"] ?? string.Empty,
                        FolderFullName = ReadFolder(query),
                        Uid = uint.TryParse(query["uid"], out uint probeUid) ? probeUid : 0
                    };
                    NotificationService.Present(toast);
                    return new { presenterRegistered = NotificationService.Presenter != null, toast.Title, toast.Body, toast.Uid };
                }

                // Saves a small text file the way attachments and exports are saved, so the path from
                // the write folder to where the user finds downloads can be checked without a message.
                case ("POST", "/downloads/probe"):
                {
                    string written = AttachmentService.UniquePath(UserDownloads.WriteFolder, query["name"] ?? "download-probe.txt");
                    File.WriteAllText(written, $"Download probe written {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    return new { writeFolder = UserDownloads.WriteFolder, written, location = UserDownloads.Publish(written) };
                }

                // Hands a link to the platform's default handler exactly as the unsubscribe chip does.
                case ("POST", "/link/open"):
                {
                    bool opened = ExternalLinkService.TryOpen(RequireQueryValue(query, "url"), out string failureReason);
                    return new { opened, failureReason };
                }

                // Answers "is this element really in the page" for anything a screenshot cannot
                // show: below the fold, inside a virtualized list, or a style that only emits CSS.
                case ("GET", "/dom"):
                {
                    string? html = await RenderedPageProbe.ReadHtmlAsync(query["selector"] ?? string.Empty);
                    return new { available = html != null, length = html?.Length ?? 0, html };
                }

                // Ends the run the way a real quit does - reason noted, then a normal exit that
                // lets ProcessExit write the run lock's closing lines. Reading those lines back
                // otherwise means clicking the tray menu on the user's screen.
                case ("POST", "/exit"):
                {
                    var reason = Enum.TryParse(query["reason"], ignoreCase: true, out AppExitReason parsed)
                        ? parsed
                        : AppExitReason.ProcessExit;
                    RunLock.NoteExitReason(reason);
                    // Delayed so this answer is on the wire before the process goes.
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(250);
                        Environment.Exit(0);
                    });
                    return new { exiting = true, reason = reason.ToString() };
                }

                // Answers "is the installed copy running" the way the deploy script does: by
                // trying to take the very lock the app holds while it is up.
                case ("GET", "/run-lock"):
                    return new
                    {
                        path = RunLock.FilePath,
                        exists = File.Exists(RunLock.FilePath),
                        text = File.Exists(RunLock.FilePath) ? File.ReadAllText(RunLock.FilePath) : null
                    };

                // Opens a page without touching the screen, so any page can be snapshot-audited.
                case ("POST", "/navigate"):
                {
                    string route = RequireQueryValue(query, "route");
                    return new { navigated = NavigationBridge.TryNavigate(route), route };
                }

                // Evaluates one expression in the page. Scrolling something into view before a
                // snapshot, reading a computed style, checking whether a script ran — all of it is
                // one call instead of a new endpoint each time.
                case ("POST", "/dom/script"):
                {
                    using var scriptReader = new StreamReader(request.InputStream);
                    string? value = await RenderedPageProbe.RunScriptAsync(await scriptReader.ReadToEndAsync());
                    return new { value };
                }

                // Clicks an element IN THE DOM. No cursor moves and no window takes focus, so a
                // control that only exists once it is opened — an expander, a menu — can be
                // audited on a machine somebody is working on.
                case ("POST", "/dom/click"):
                {
                    string selector = JsonSerializer.Serialize(RequireQueryValue(query, "selector"));
                    string? result = await RenderedPageProbe.RunScriptAsync(
                        $"(function(){{var e=document.querySelector({selector});if(!e)return 'not found';e.click();return 'clicked';}})()");
                    return new { result };
                }

                // Dispatches a real mouse event on an element — the only way to exercise the
                // side-button script itself rather than the C# it ends up calling.
                case ("POST", "/dom/mouse"):
                {
                    string selector = JsonSerializer.Serialize(RequireQueryValue(query, "selector"));
                    int button = int.TryParse(query["button"], out int parsedButton) ? parsedButton : 3;
                    string? result = await RenderedPageProbe.RunScriptAsync(
                        $"(function(){{var e=document.querySelector({selector});if(!e)return 'not found';"
                        + $"e.dispatchEvent(new MouseEvent('auxclick',{{button:{button},bubbles:true,cancelable:true}}));return 'dispatched';}})()");
                    return new { result, button };
                }

                // Fires a side-button action without a mouse, so the behavior is testable without
                // touching the cursor on a machine the user is sitting at.
                case ("POST", "/mouse"):
                {
                    string action = query["action"] ?? MouseActionService.CopyAction;
                    string status = await MouseActionService.RunAsync(action, query["value"] ?? string.Empty);
                    return new { action, status };
                }

                // What the steps of the open path actually cost. "Opening this message pins a core
                // for five seconds" has as many plausible explanations as it has steps, and every
                // one of them looks cheap in the source; this is the only way to name the real one.
                case ("GET", "/timings"):
                {
                    var spans = PerfTrace.Recent;
                    return new
                    {
                        spans,
                        slowest = spans.OrderByDescending(static s => s.Milliseconds).Take(ReadTake(query, 10)),
                        totalMs = spans.Sum(static s => s.Milliseconds)
                    };
                }

                case ("POST", "/timings/clear"):
                    PerfTrace.Clear();
                    return new { cleared = true };

                case ("GET", "/logs"):
                {
                    string? filter = query["filter"];
                    IEnumerable<string> lines = Logger.AllLogs;
                    if (!string.IsNullOrEmpty(filter))
                        lines = lines.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase));
                    return lines.TakeLast(ReadTake(query, 100)).ToArray();
                }

            default:
                throw new InvalidOperationException($"Unknown endpoint: {request.HttpMethod} {request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant()}");
        }
    }
}