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

                // Dispatches a real mouse event on an element or at (x, y) coordinates, including click + drag simulation.
                case ("POST", "/dom/mouse"):
                case ("POST", "/dom/drag"):
                case ("POST", "/input/mouse"):
                {
                    string routePath = request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? string.Empty;
                    double x = 0, y = 0;
                    bool hasCoords = double.TryParse(query["x"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)
                                  && double.TryParse(query["y"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y);

                    if (!hasCoords && query["selector"] is { Length: > 0 } selText)
                    {
                        string selector = JsonSerializer.Serialize(selText);
                        int button = int.TryParse(query["button"], out int parsedButton) ? parsedButton : 3;
                        string? result = await RenderedPageProbe.RunScriptAsync(
                            $"(function(){{var e=document.querySelector({selector});if(!e)return 'not found';"
                            + $"e.dispatchEvent(new MouseEvent('auxclick',{{button:{button},bubbles:true,cancelable:true}}));return 'dispatched';}})()");
                        return new { result, button };
                    }

                    string action = (query["action"] ?? (routePath == "/dom/drag" || query["toX"] != null ? "drag" : "click")).ToLowerInvariant();
                    string btnName = (query["button"] ?? "left").ToLowerInvariant();
                    (string cdpButton, int domButton, int domButtons) = btnName switch
                    {
                        "right" or "2" or "context" => ("right", 2, 2),
                        "middle" or "1" or "aux" => ("middle", 1, 4),
                        "back" or "3" or "mouse4" => ("back", 3, 8),
                        "forward" or "4" or "mouse5" => ("forward", 4, 16),
                        _ => ("left", 0, 1)
                    };

                    string invX = x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                    string invY = y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

                    if (action == "drag")
                    {
                        double toX = double.TryParse(query["toX"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double tx) ? tx : x;
                        double toY = double.TryParse(query["toY"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ty) ? ty : y;
                        int steps = int.TryParse(query["steps"], out int parsedSteps) && parsedSteps > 0 ? parsedSteps : 12;

                        await RenderedPageProbe.CallCdpAsync("Input.dispatchMouseEvent",
                            $"{{\"type\":\"mouseMoved\",\"x\":{invX},\"y\":{invY},\"button\":\"none\",\"buttons\":0}}");
                        string? cdpDown = await RenderedPageProbe.CallCdpAsync("Input.dispatchMouseEvent",
                            $"{{\"type\":\"mousePressed\",\"x\":{invX},\"y\":{invY},\"button\":\"{cdpButton}\",\"buttons\":{domButtons},\"clickCount\":1}}");

                        string? startInfo = await RenderedPageProbe.RunScriptAsync(
                            $"(function(){{var el=document.elementFromPoint({invX},{invY})||document.body;"
                            + (cdpDown == null
                                ? $"var opts={{bubbles:true,cancelable:true,clientX:{invX},clientY:{invY},button:{domButton},buttons:{domButtons},pointerId:1,isPrimary:true}};"
                                  + $"el.dispatchEvent(new PointerEvent('pointerdown',opts));el.dispatchEvent(new MouseEvent('mousedown',opts));"
                                : "")
                            + $"return el.tagName+(el.className?'.'+String(el.className).trim().replace(/\\s+/g,'.'):'');}})()");

                        for (int i = 1; i <= steps; i++)
                        {
                            double curX = x + (toX - x) * i / steps;
                            double curY = y + (toY - y) * i / steps;
                            string cx = curX.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                            string cy = curY.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                            string? cdpMove = await RenderedPageProbe.CallCdpAsync("Input.dispatchMouseEvent",
                                $"{{\"type\":\"mouseMoved\",\"x\":{cx},\"y\":{cy},\"button\":\"{cdpButton}\",\"buttons\":{domButtons}}}");
                            if (cdpMove == null)
                            {
                                await RenderedPageProbe.RunScriptAsync(
                                    $"(function(){{var el=document.elementFromPoint({cx},{cy})||document.body;"
                                    + $"var opts={{bubbles:true,cancelable:true,clientX:{cx},clientY:{cy},button:{domButton},buttons:{domButtons},pointerId:1,isPrimary:true}};"
                                    + $"el.dispatchEvent(new PointerEvent('pointermove',opts));el.dispatchEvent(new MouseEvent('mousemove',opts));"
                                    + $"window.dispatchEvent(new PointerEvent('pointermove',opts));window.dispatchEvent(new MouseEvent('mousemove',opts));"
                                    + $"return 'moved';}})()");
                            }
                            await Task.Delay(14);
                        }

                        string invToX = toX.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                        string invToY = toY.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                        string? endInfo = await RenderedPageProbe.RunScriptAsync(
                            $"(function(){{var el=document.elementFromPoint({invToX},{invToY})||document.body;"
                            + $"return el.tagName+(el.className?'.'+String(el.className).trim().replace(/\\s+/g,'.'):'');}})()");

                        string? cdpUp = await RenderedPageProbe.CallCdpAsync("Input.dispatchMouseEvent",
                            $"{{\"type\":\"mouseReleased\",\"x\":{invToX},\"y\":{invToY},\"button\":\"{cdpButton}\",\"buttons\":0,\"clickCount\":1}}");
                        if (cdpUp == null)
                        {
                            await RenderedPageProbe.RunScriptAsync(
                                $"(function(){{var el=document.elementFromPoint({invToX},{invToY})||document.body;"
                                + $"var opts={{bubbles:true,cancelable:true,clientX:{invToX},clientY:{invToY},button:{domButton},buttons:0,pointerId:1,isPrimary:true}};"
                                + $"el.dispatchEvent(new PointerEvent('pointerup',opts));el.dispatchEvent(new MouseEvent('mouseup',opts));"
                                + $"window.dispatchEvent(new PointerEvent('pointerup',opts));window.dispatchEvent(new MouseEvent('mouseup',opts));"
                                + $"return 'up';}})()");
                        }

                        return new { action = "drag", from = new { x, y, element = startInfo }, to = new { x = toX, y = toY, element = endInfo }, steps };
                    }

                    if (action is "move" or "hover")
                    {
                        string? cdpHover = await RenderedPageProbe.CallCdpAsync("Input.dispatchMouseEvent",
                            $"{{\"type\":\"mouseMoved\",\"x\":{invX},\"y\":{invY},\"button\":\"none\",\"buttons\":0}}");
                        string? hoverEl = await RenderedPageProbe.RunScriptAsync(
                            $"(function(){{var el=document.elementFromPoint({invX},{invY})||document.body;"
                            + (cdpHover == null
                                ? $"var opts={{bubbles:true,cancelable:true,clientX:{invX},clientY:{invY}}};"
                                  + $"el.dispatchEvent(new PointerEvent('pointerover',opts));el.dispatchEvent(new PointerEvent('pointerenter',opts));"
                                  + $"el.dispatchEvent(new MouseEvent('mouseover',opts));el.dispatchEvent(new MouseEvent('mouseenter',opts));el.dispatchEvent(new MouseEvent('mousemove',opts));"
                                : "")
                            + $"return el.tagName+(el.className?'.'+String(el.className).trim().replace(/\\s+/g,'.'):'');}})()");
                        return new { action = "hover", x, y, element = hoverEl };
                    }

                    int clickCount = action == "dblclick" ? 2 : 1;
                    await RenderedPageProbe.CallCdpAsync("Input.dispatchMouseEvent",
                        $"{{\"type\":\"mouseMoved\",\"x\":{invX},\"y\":{invY},\"button\":\"none\",\"buttons\":0}}");
                    await RenderedPageProbe.CallCdpAsync("Input.dispatchMouseEvent",
                        $"{{\"type\":\"mousePressed\",\"x\":{invX},\"y\":{invY},\"button\":\"{cdpButton}\",\"buttons\":{domButtons},\"clickCount\":{clickCount}}}");
                    await RenderedPageProbe.CallCdpAsync("Input.dispatchMouseEvent",
                        $"{{\"type\":\"mouseReleased\",\"x\":{invX},\"y\":{invY},\"button\":\"{cdpButton}\",\"buttons\":0,\"clickCount\":{clickCount}}}");

                    string? targetInfo = await RenderedPageProbe.RunScriptAsync(
                        $"(function(){{var el=document.elementFromPoint({invX},{invY})||document.body;"
                        + $"var opts={{bubbles:true,cancelable:true,clientX:{invX},clientY:{invY},button:{domButton},buttons:0, detail:{clickCount}}};"
                        + (domButton == 2 || action == "contextmenu" || action == "rightclick"
                            ? $"el.dispatchEvent(new MouseEvent('contextmenu',opts));"
                            : domButton == 1 || action == "middleclick"
                                ? $"el.dispatchEvent(new MouseEvent('mouseup',opts));el.dispatchEvent(new MouseEvent('auxclick',opts));"
                                : action == "dblclick"
                                    ? $"el.dispatchEvent(new MouseEvent('dblclick',opts));"
                                    : "")
                        + $"return el.tagName+(el.className?'.'+String(el.className).trim().replace(/\\s+/g,'.'):'');}})()");

                    return new { action, button = cdpButton, x, y, element = targetInfo };
                }

                // Simulates a keyboard keystroke or text typing at an optional (x, y) coordinate inside the window.
                case ("POST", "/dom/key"):
                case ("POST", "/input/key"):
                {
                    double x = 0, y = 0;
                    bool hasCoords = double.TryParse(query["x"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)
                                  && double.TryParse(query["y"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y);
                    string key = query["key"] ?? "Enter";
                    string? text = query["text"];
                    bool ctrl = bool.TryParse(query["ctrl"], out bool c) && c;
                    bool shift = bool.TryParse(query["shift"], out bool s) && s;
                    bool alt = bool.TryParse(query["alt"], out bool a) && a;
                    bool clickFirst = !bool.TryParse(query["click"], out bool cl) || cl;

                    int modifiers = (alt ? 1 : 0) | (ctrl ? 2 : 0) | (shift ? 8 : 0);
                    if (hasCoords && clickFirst)
                    {
                        string ix = x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                        string iy = y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                        await RenderedPageProbe.CallCdpAsync("Input.dispatchMouseEvent",
                            $"{{\"type\":\"mousePressed\",\"x\":{ix},\"y\":{iy},\"button\":\"left\",\"buttons\":1,\"clickCount\":1}}");
                        await RenderedPageProbe.CallCdpAsync("Input.dispatchMouseEvent",
                            $"{{\"type\":\"mouseReleased\",\"x\":{ix},\"y\":{iy},\"button\":\"left\",\"buttons\":0,\"clickCount\":1}}");
                    }

                    string keyJson = JsonSerializer.Serialize(key);
                    string textJson = JsonSerializer.Serialize(text ?? string.Empty);
                    string coordFind = hasCoords
                        ? $"document.elementFromPoint({x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)},{y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)})"
                        : "null";

                    string? domTarget = await RenderedPageProbe.RunScriptAsync(
                        $"(function(){{var el={coordFind}||document.activeElement||document.body;"
                        + $"if(el&&typeof el.focus==='function')el.focus();"
                        + $"var k={keyJson};var t={textJson};"
                        + $"var evInit={{key:k,code:k.length===1?'Key'+k.toUpperCase():k,ctrlKey:{(ctrl ? "true" : "false")},shiftKey:{(shift ? "true" : "false")},altKey:{(alt ? "true" : "false")},bubbles:true,cancelable:true}};"
                        + $"el.dispatchEvent(new KeyboardEvent('keydown',evInit));"
                        + $"if(t&&('value' in el)){{el.value=(el.value||'')+t;el.dispatchEvent(new Event('input',{{bubbles:true}}));el.dispatchEvent(new Event('change',{{bubbles:true}}));}}"
                        + $"el.dispatchEvent(new KeyboardEvent('keyup',evInit));"
                        + $"return el.tagName+(el.className?'.'+String(el.className).trim().replace(/\\s+/g,'.'):'');}})()");

                    await RenderedPageProbe.CallCdpAsync("Input.dispatchKeyEvent",
                        $"{{\"type\":\"keyDown\",\"key\":{keyJson},\"modifiers\":{modifiers}}}");
                    await RenderedPageProbe.CallCdpAsync("Input.dispatchKeyEvent",
                        $"{{\"type\":\"keyUp\",\"key\":{keyJson},\"modifiers\":{modifiers}}}");

                    return new { dispatched = true, x = hasCoords ? (double?)x : null, y = hasCoords ? (double?)y : null, key, text, ctrl, shift, alt, target = domTarget };
                }

                // Captures a PNG screenshot of the app window in the background without bringing it to the foreground.
                case ("GET", "/screenshot"):
                case ("POST", "/screenshot"):
                {
                    byte[]? pngBytes = await RenderedPageProbe.CaptureScreenshotAsync();
                    if (pngBytes == null || pngBytes.Length == 0)
                        throw new InvalidOperationException("Screenshot capture returned empty bytes.");

                    string? savePath = query["path"];
                    if (!string.IsNullOrWhiteSpace(savePath))
                    {
                        string fullPath = Path.GetFullPath(savePath);
                        if (Path.GetDirectoryName(fullPath) is { Length: > 0 } dir)
                            Directory.CreateDirectory(dir);
                        await File.WriteAllBytesAsync(fullPath, pngBytes);
                        return new { saved = true, path = fullPath, bytes = pngBytes.Length };
                    }

                    return new { saved = false, bytes = pngBytes.Length, base64 = Convert.ToBase64String(pngBytes) };
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

                case ("GET", "/ui-layout"):
                {
                    string? surfaceFilter = query["surface"];
                    var surfaces = string.IsNullOrWhiteSpace(surfaceFilter)
                        ? UiSurfaceIds.AllSurfaces
                        : [surfaceFilter];
                    return new
                    {
                        contextMenu = new
                        {
                            isOpen = ContextMenuService.IsOpen,
                            title = ContextMenuService.Title,
                            surfaceId = ContextMenuService.SurfaceId,
                            isEditMode = ContextMenuService.IsEditMode,
                            items = ContextMenuService.Items.Select(static i => new
                            {
                                id = i.EffectiveActionId,
                                label = i.Label,
                                hasChildren = i.HasChildren,
                                children = i.Children?.Select(static c => new { id = c.EffectiveActionId, label = c.Label }).ToArray()
                            }).ToArray()
                        },
                        accountFolders = AccountFolderStore.GetFlattenedTree().Select(static f => new
                        {
                            f.Folder.Id,
                            f.Folder.Name,
                            f.Folder.ParentFolderId,
                            f.Folder.SortOrder,
                            f.Folder.IsExpanded,
                            f.Depth
                        }).ToArray(),
                        accounts = AccountStore.Accounts.Select(static a => new
                        {
                            a.Id,
                            a.EmailAddress,
                            a.FolderId,
                            a.SortOrder
                        }).ToArray(),
                        selectedAccountMailFolders = MailUiState.SelectedAccount is { } selAcc
                            ? FolderTreeService.Build(MessageStore.GetFolders(selAcc.Id)).Select(static n => new
                            {
                                n.Folder.FullName,
                                n.Folder.DisplayName,
                                n.Folder.SortOrder
                            }).ToArray()
                            : [],
                        listView = new
                        {
                            mailSort = ListViewState.MailSort.ToString(),
                            mailSortDir = ListViewState.MailSortDirection.ToString(),
                            senderSort = ListViewState.SenderSort.ToString(),
                            senderSortDir = ListViewState.SenderSortDirection.ToString(),
                            filters = ListViewState.ActiveFilters.Select(static f => new { f.Id, f.Condition.Field, f.Condition.Operator, f.Condition.Value }).ToArray()
                        },
                        surfaces = surfaces.Select(static s =>
                        {
                            var snap = UiCustomizationStore.GetSnapshot(s);
                            return new
                            {
                                surfaceId = s,
                                customized = UiCustomizationStore.IsSurfaceCustomized(s),
                                orderedIds = snap.OrderedIds,
                                hiddenIds = snap.HiddenIds,
                                addedIds = snap.AddedIds,
                                submenus = snap.Submenus
                            };
                        }).ToArray()
                    };
                }

                case ("POST", "/ui-layout"):
                {
                    if (string.Equals(query["reset"], "all", StringComparison.OrdinalIgnoreCase))
                    {
                        UiCustomizationStore.ResetAll();
                        return new { reset = "all" };
                    }
                    if (query["reset"] is { Length: > 0 } resetSurface)
                    {
                        UiCustomizationStore.ResetSurface(resetSurface);
                        return new { reset = resetSurface };
                    }
                    return new { ok = true };
                }

                case ("POST", "/seed/demo"):
                {
                    TagStore.ColorOf("JetTag");
                    TagStore.ColorOf("Urgent");
                    TagStore.ColorOf("Review");

                    var workFolder = AccountFolderStore.Folders.FirstOrDefault(static f => string.Equals(f.Name, "Work", StringComparison.OrdinalIgnoreCase))
                        ?? AccountFolderStore.CreateFolder("Work");

                    MailAccountData EnsureAccount(string id, string email, string displayName, string folderId, string color, int sortOrder)
                    {
                        var existing = AccountStore.GetById(id);
                        if (existing != null) return existing;
                        var acc = new MailAccountData
                        {
                            Id = id,
                            EmailAddress = email,
                            DisplayName = displayName,
                            FolderId = folderId,
                            Protocol = IncomingProtocol.Imap,
                            IncomingHost = "imap.demo.invalid",
                            IncomingPort = 993,
                            SmtpHost = "smtp.demo.invalid",
                            SmtpPort = 465,
                            Enabled = false,
                            ColorHex = color,
                            SortOrder = sortOrder
                        };
                        AccountStore.Save(acc);
                        return acc;
                    }

                    var alice = EnsureAccount("demo-alice", "alice@lovely.test", "Alice Inbox", AccountFolderStore.DefaultFolderId, "#F472B6", 1);
                    var bob = EnsureAccount("demo-bob", "bob@lovely.test", "Bob Secondary", AccountFolderStore.DefaultFolderId, "#38BDF8", 2);
                    var gamma = EnsureAccount("demo-gamma", "gamma@lovely.test", "Gamma Backup", AccountFolderStore.DefaultFolderId, "#4ADE80", 3);
                    var ops = EnsureAccount("demo-ops", "ops@work.test", "Work Ops", workFolder.Id, "#FBBF24", 4);

                    string localProjects = MessageStore.LocalFolderPrefix + "Projects";
                    var demoFolders = new[]
                    {
                        new MailFolderData { AccountId = alice.Id, FullName = MessageStore.InboxFullName, DisplayName = "Inbox", Role = FolderRole.Inbox, Selectable = true, TotalCount = 3, UnreadCount = 2, LastSyncedUtc = DateTime.UtcNow, SortOrder = 1 },
                        new MailFolderData { AccountId = alice.Id, FullName = "Archive", DisplayName = "Archive", Role = FolderRole.Archive, Selectable = true, TotalCount = 0, UnreadCount = 0, LastSyncedUtc = DateTime.UtcNow, SortOrder = 2 },
                        new MailFolderData { AccountId = alice.Id, FullName = localProjects, DisplayName = "Projects", Role = FolderRole.None, Selectable = true, TotalCount = 0, UnreadCount = 0, LastSyncedUtc = DateTime.UtcNow, SortOrder = 3 },
                        new MailFolderData { AccountId = alice.Id, FullName = "Trash", DisplayName = "Trash", Role = FolderRole.Trash, Selectable = true, TotalCount = 0, UnreadCount = 0, LastSyncedUtc = DateTime.UtcNow, SortOrder = 4 }
                    };
                    foreach (var f in demoFolders)
                        MessageStore.SaveFolder(f);

                    MessageStore.SaveFolder(new MailFolderData
                    {
                        AccountId = bob.Id,
                        FullName = MessageStore.InboxFullName,
                        DisplayName = "Inbox",
                        Role = FolderRole.Inbox,
                        Selectable = true,
                        TotalCount = 0,
                        UnreadCount = 0,
                        LastSyncedUtc = DateTime.UtcNow,
                        SortOrder = 1
                    });

                    static byte[] BuildMime(string fromName, string fromAddr, string toAddr, string subject, string htmlBody, DateTime dateUtc)
                    {
                        var msg = new MimeMessage();
                        msg.From.Add(new MailboxAddress(fromName, fromAddr));
                        msg.To.Add(MailboxAddress.Parse(toAddr));
                        msg.Subject = subject;
                        msg.Date = new DateTimeOffset(dateUtc, TimeSpan.Zero);
                        var builder = new BodyBuilder
                        {
                            TextBody = "Plain text fallback: " + subject,
                            HtmlBody = htmlBody
                        };
                        msg.Body = builder.ToMessageBody();
                        using var ms = new MemoryStream();
                        msg.WriteTo(ms);
                        return ms.ToArray();
                    }

                    DateTime now = DateTime.UtcNow;
                    var seedMessages = new (uint Uid, string FromName, string FromAddr, string Subject, string Preview, bool Unread, string Html)[]
                    {
                        (101, "Ada Lovelace", "ada@analytical.org", "Analytical Engine Architecture Notes", "Reviewing the pure C# component hierarchy and layout tokens.", true, "<p>Hello Alice,</p><p>Here are the architecture notes for the Analytical Engine module.</p>"),
                        (102, "Grace Hopper", "grace@compiler.org", "Compiler Verification Report", "Zero warnings and deterministic CSS class constants.", true, "<p>Hi Alice,</p><p>All compiler checks passed with zero warnings.</p>"),
                        (103, "Alan Turing", "alan@enigma.org", "Cryptographic Vault Review", "AES-GCM bundle check completed cleanly.", false, "<p>Greetings,</p><p>The migration bundle encryption test finished.</p>")
                    };

                    var summaries = new List<MailMessageSummary>();
                    foreach (var m in seedMessages)
                    {
                        byte[] raw = BuildMime(m.FromName, m.FromAddr, alice.EmailAddress, m.Subject, m.Html, now.AddMinutes(-(int)m.Uid));
                        MessageStore.SaveFullMessage(alice.Id, MessageStore.InboxFullName, m.Uid, raw);
                        summaries.Add(new MailMessageSummary
                        {
                            Uid = m.Uid,
                            MessageId = $"<seed-{m.Uid}@lovely.test>",
                            FromName = m.FromName,
                            FromAddress = m.FromAddr,
                            ToAddresses = alice.EmailAddress,
                            Subject = m.Subject,
                            PreviewText = m.Preview,
                            DateUtc = now.AddMinutes(-(int)m.Uid),
                            SizeBytes = raw.Length,
                            Flags = m.Unread ? MailFlags.None : MailFlags.Seen,
                            Tags = m.Uid == 101 ? ["Review"] : []
                        });
                    }
                    MessageStore.UpsertSummaries(alice.Id, MessageStore.InboxFullName, summaries);

                    MailUiState.SelectAccount(alice, restoreFolder: false);
                    MailUiState.SelectFolder(demoFolders[0]);
                    MailUiState.OpenMessageInReader(summaries[0], new MailMessageIdentity(alice.Id, MessageStore.InboxFullName, summaries[0].Uid));

                    return new
                    {
                        seeded = true,
                        accounts = AccountStore.Accounts.Select(static a => new { a.Id, a.EmailAddress, a.FolderId, a.SortOrder }).ToArray(),
                        tags = TagStore.AllTags.ToArray()
                    };
                }

            default:
                throw new InvalidOperationException($"Unknown endpoint: {request.HttpMethod} {request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant()}");
        }
    }
}