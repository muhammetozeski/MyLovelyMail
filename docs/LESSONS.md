# Lessons learned

- **Release uploads can leave an asset in `starter` state.** Verify the release asset's state and size before publication. During the v1.40.1 delivery, HTTP/2 uploads stalled while HTTP/1.1 completed both the Android and Windows packages. `Release.ps1` uses REST release IDs for draft assets and temporarily selects HTTP/1.1 during transfer, restoring the caller's environment afterward. Resume the prepared packages rather than rebuilding them because an upload failed.

- **Starting the installed program may honor `StartMinimized`.** A second ordinary launch uses `SingleInstance.Claim` to restore the existing window. This opened the updated v1.40.1 installation without changing the user's startup setting.

Short "problem → solution" notes so the same wall is never hit twice.

- **Parameterless `*Css.razor` components do not re-render on theme change.** Blazor skips
  re-rendering children whose parameters did not change, so a `ThemeManager.Apply` left every
  emitted `<style>` block on the old palette. Solution: `ThemedComponentBase` (UI/Architecture)
  subscribes to the `OnThemeChanged` event and calls `StateHasChanged`; every parameterless Css
  component must inherit it.

- **The template never rendered `MainLayoutCss`.** `MainLayout.razor` did not include
  `<MainLayoutCss />`, so the aurora background, fonts and base body styles were silently missing
  on every platform. Solution: render it at the end of `MainLayout.razor`.

- **`InteractiveAuto` on the Web host moves interactivity into WASM after the first visit.**
  Clicks then run inside the browser: `File.WriteAllText` writes into the browser's virtual file
  system and the server-side disk never changes, which made settings look "not persisted" while
  the UI happily updated. The MAUI app is unaffected (BlazorWebView runs in-process). Solution:
  the Web preview host pins `@rendermode="InteractiveServer"` so preview behavior matches the
  desktop app.

- **The template's `NavigationGenerator` emitted no `BottomNavigationItems`,** but
  `AppBottomNav.razor` consumes it — the template did not compile as shipped. Solution: the
  generator now emits a `(Title, Icon, Link)[]` array after the per-page classes.

- **Windows-invalid characters in Brainstorm idea file names** (`:` and `/` in titles) make
  `Set-Content` fail with misleading binding errors. Keep idea titles free of `\/:*?"<>|`.

- **Browser-automation clicks race Blazor Server's SignalR round-trip.** Setting an input via JS
  and clicking a disabled-until-bound button in the same script fails silently — the button is
  still disabled when the click lands. Dispatch the input event, wait for the round-trip, then click.

- **A running MyLovelyMail instance locks MainProject.dll** and `dotnet build` fails with MSB3027
  after 10 retries. Always `Stop-Process -Name MyLovelyMail` before rebuilding the head project.

- **`CopyFromScreen` snapshots capture whatever covers the window.** Use `PrintWindow` with
  `PW_RENDERFULLCONTENT` (flag 2) instead — it renders the window's own surface even when it is
  behind other windows, and it captures WebView2 content correctly.

- **Servers without SPECIAL-USE (RFC 6154) report no folder roles** (smtp4dev, many others), which
  duplicated Sent handling. `ImapSyncService.GuessRoleFromName` maps the conventional names.

- **Bulk source edits via PowerShell `string.Replace` fail SILENTLY.** When the old text does not
  match byte-for-byte (indentation, earlier edits), `.Replace` is a no-op and the script still
  reports success — an entire batch of log insertions "landed" without a single line changing.
  Use the Edit tool for source changes: it errors on a miss instead of lying.

- **MailKit `IdleAsync` returns only when its done-token fires.** Setting a flag inside
  `CountChanged` is not enough — cancel the done-token in the handler, or the "instant" push
  waits out the full reissue interval before anyone looks at the flag.

- **smtp4dev is a full local IMAP+SMTP loop for end-to-end tests**: send over SMTP :2525, sync it
  back over IMAP :1143, any credentials accepted. Combined with the DEBUG-only REST API
  (`DebugApi`, http://127.0.0.1:52539) the whole receive/send path is verifiable headlessly.

## Never drive the UI with screen clicks — extend the DebugApi instead
- Problem: verifying UI states (open compose, click a folder) by simulating mouse clicks moves the user's cursor and steals focus while they are using the machine.
- Solution: the DEBUG REST API (127.0.0.1:52539) is the only sanctioned way to drive the running app. When a UI state has no endpoint, add one (like POST /compose) instead of reaching for SetCursorPos/mouse_event. Click-free PrintWindow snapshots remain fine; anything that touches cursor, keyboard or foreground focus is banned.

- **`smtp4dev` started with `Start-Process` from a tool call dies with that call.** The SMTP
  session opens, reports `MessageCount: 0`, and the next request finds nothing listening. Start it
  as a background command that outlives the call instead.

- **smtp4dev's mailbox has INBOX and Sent only, and `CREATE` is refused** ("Folders are not
  supported"). Useful in itself: it is the cheapest way to test that a folder pass survives a
  server saying no.

- **An IMAP `<n>:*` fetch always returns the mailbox's last message**, even when nothing new
  arrived (RFC 3501). Anything that must happen once per arrival has to hang off "uid greater than
  the last seen one", never off "the fetch returned rows".

- **A `<script>` inside an `srcdoc` preview that calls `document.write` after parsing wipes the
  frame.** The preview looked broken; the signature was fine. Test preview frames with a script
  that writes into an element, not with `document.write`.

## Driving the page without touching the mouse
- `POST /dom/click?selector=` clicks an element **in the DOM**, `POST /dom/mouse?selector=&button=`
  dispatches a real `auxclick` (so the side-button script itself is exercised, not just the C# it
  calls), and `POST /dom/script` evaluates one expression in the page — scrolling something into
  view before a snapshot, reading a computed style, checking whether a script ran.
- None of them move the cursor or take focus, so they are safe on a machine somebody is using.
  `RenderedPageProbe.ScriptRunner` is the hook; the head registers it in DEBUG only.

- **Android .NET `CheckCertificateRevocation = true` breaks Gmail and other OCSP-less CAs.**
  On Android, .NET validates revocation strictly through OCSP with no CRL fallback. Google Trust
  Services' intermediate omits an OCSP responder URL, so MailKit's default revocation check fails
  every direct connection with `SslHandshakeException` (`Chain_RevocationStatusUnknown`:
  `Certificate does not specify OCSP responder`). Solution: `MailConnections.DirectRevocationCheck`
  is `!OperatingSystem.IsAndroid()` and is applied on both direct connections and direct triage
  probes (`ConnectTriageService.ProbeWithAsync`), while keeping full chain, hostname and expiry
  validation intact.

- **CSS Grid `1fr` tracks expand past phone viewports when a child scrolls horizontally.**
  Under `MailResponsiveCss` (`OnePane` and `TwoPane`), `1fr` allows the grid track to grow to the
  widest child's `min-content` (such as `.mail-sidebar-nav`), clipping the search bar and toolbars
  off the right edge of the phone screen. Solution: use `minmax(0, 1fr)` so the track stays capped
  at the viewport width while the inner strip scrolls horizontally.

- **Android 11+ Scoped Storage blocks reading raw paths under `/sdcard/Download`.**
  Typing a path into Migration bundle import cannot open files copied to Downloads by another app.
  Solution: `UserFilePicker` (bridged to MAUI's `FilePicker.Default.PickAsync` in `MauiProgram`)
  lets the user pick the `.lovelybundle` file through the system document picker, which copies it
  into app-readable cache space.

- **MAUI `10.0.110+` lets a `TaskbarIcon` added after `BlazorWebView` swallow all Windows mouse input.**
  On `Microsoft.Maui` `10.0.20`–`10.0.70`, `RootLayout.Add(trayIcon)` (from `H.NotifyIcon.Maui`)
  did not block `BlazorWebView` clicks. Starting with `10.0.110` (and `10.0.130`), placing
  `TaskbarIcon` after `BlazorWebView` in the same `Grid` cell swallows every Win32 mouse event
  before it reaches WebView2 (`click: 0`), even though CDP synthetic clicks still pass. Solution:
  keep `TaskbarIcon` non-hit-testable (`InputTransparent = true`, `0×0` size) and insert it behind
  `BlazorWebView` via `RootLayout.Insert(0, trayIcon)`.

- **Windows auto-maximize and single-bar themed title bars on MAUI 10.**
  Restoring saved full-screen bounds (`0, 0, 1536, 816`) as a non-maximized window leaves Win11
  normal-window resize borders and edge gaps; calling `ClampToWorkArea` while maximized also drops
  the window out of maximized state. Use `OverlappedPresenter.Maximize()` on startup/first show,
  skip `SizeChanged` persistence and `ClampToWorkArea` while maximized, and style the title bar
  through MAUI's `Window.TitleBar` plus `AppWindow.TitleBar` button colors (setting
  `AppWindow.TitleBar.ExtendsContentIntoTitleBar = false` alone leaves MAUI's own bar underneath as
  a duplicate bar).
