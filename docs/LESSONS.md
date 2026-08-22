# Lessons learned

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
