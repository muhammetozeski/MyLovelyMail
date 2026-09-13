# My Lovely Mail

![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![C%23](https://img.shields.io/badge/C%23-14-239120)
![UI](https://img.shields.io/badge/UI-MAUI%20Blazor%20Hybrid-68217A)
![Platform](https://img.shields.io/badge/platform-Windows%2011%20%C2%B7%20Android%2011%2B-0078D4)
![Protocols](https://img.shields.io/badge/mail-IMAP%20%C2%B7%20POP3%20%C2%B7%20SMTP-EC6FA9)
![Tor](https://img.shields.io/badge/transport-Tor%20SOCKS5-7D4698)

A mail client for Windows and Android with a pastel, glass-styled interface. It receives mail over
IMAP or POP3, sends over SMTP, stores full messages on disk (only lightweight summaries stay in
RAM), and renders HTML mail inside a sandboxed frame after stripping active content.

The UI is Blazor running inside .NET MAUI (BlazorWebView). All CSS values — colors, spacing,
typography — are emitted from C# constants, so the same tokens are usable from both C# and markup,
and switching the theme re-skins the whole app at runtime.

| Inbox | Reading pane |
| --- | --- |
| ![Inbox](docs/screenshots/inbox.png) | ![Reader](docs/screenshots/reader.png) |

## Features

**Accounts and sync**
- IMAP (incremental UID-based sync, UIDVALIDITY invalidation) and POP3 accounts; SMTP sending
- Account wizard with server presets (Gmail, Outlook, Yahoo, iCloud, Yandex, custom), a
  connection test, and a per-account accent color that tints the workspace
- Periodic background sync with a configurable interval; manual sync from the UI or tray
- Folder roles resolved from IMAP SPECIAL-USE, with name-based fallback for servers without it
- All network calls go through one Polly pipeline (retry with backoff, per-attempt and total timeouts)
- Per-account **Tor-only** mode: an account can be marked as reachable through Tor and nothing else

**Tor transport**
- Any account can be switched to Tor-only. Its IMAP/POP3/SMTP sessions are carried by the Tor
  SOCKS5 proxy, and the server name is resolved by Tor (SOCKS5 `DOMAINNAME`) rather than by this
  machine, so the provider is not revealed by a local DNS query
- The SOCKS port is proven to be Tor before it is used, via Tor's own `RESOLVE` SOCKS extension
  (command `0xF0`) — a general-purpose SOCKS5 proxy has to reject that command with `0x07`
- On Windows Tor is found in this order: a tor the app started, the configured host/port, the tor
  daemon's 9050, a running Tor Browser's 9150. If none answers, the app can start a tor of its own
  on a free port under `AppCache\Tor` and wait for its bootstrap
- On Android the app carries its own tor (`libtor.so` from the Briar project's `tor-android` build,
  see `MainProject\Platforms\Android\Tor\README.md`) and starts it first; a running Orbot on 9050 is
  used only when that tor cannot start
- Per-account circuit isolation through SOCKS username/password (`IsolateSOCKSAuth`), so accounts
  never share a circuit and a failed connection can be retried on a fresh one
- A connection climbs a six-rung ladder — known endpoint, fresh circuit, the app's own SOCKS5
  client, rediscovery, and finally a tor started by the app — under a Polly pipeline with its own
  patient budget. Every rung is a different route *through* Tor; none is a route around it

**Settings with live inheritance**
- Global settings persist as a plain `key = value` text file
- Every account holds `InheritedSetting<T>` children: until a value is overridden for that
  account, it follows the global value live; only overridden keys are written to disk
- A per-account settings page shows each value with an inherited/overridden badge and a reset button

**Reading**
- HTML bodies rendered in a sandboxed iframe after sanitization (scripts, event handlers,
  `javascript:` URLs, embeds and forms are stripped; `cid:` images are inlined as data URIs)
- Remote images blocked by default with a per-message "show once" override
- Attachment strip: save one or all attachments into Downloads
- Mark-as-read on open (optionally delayed), conversation-independent unread tracking

**Organization**
- Filter rules: conditions (from/to/subject/preview/domain/size/attachment, AND-OR, regex) and
  actions (move to a server or local folder, mark read/important/starred, mute, per-rule
  notification sound, add tag), edited in a dedicated page, executed during sync
- Colored tags with a picker; tag chips on list rows; tag search
- Cross-folder search with `from:` `to:` `tag:` `has:attachment` `is:unread` `is:starred`
  operators and saved-search chips
- Multi-select (checkboxes, Ctrl+Click, Ctrl+A) with a bulk actions bar
- Keyboard triage: J/K navigation, Enter, U (read), S (star), I (important), Delete, `?` for help

**Composing**
- Reply / reply-all / forward with quoted bodies parsed from the cached MIME
- Drafts autosave into a local Drafts folder after ~3 s idle; drafts reopen in the compose pane
- Sent messages are appended to the server Sent folder (or a local one for POP3)

**Desktop integration (Windows)**
- System tray icon with menu, close-to-tray, start minimized, start-with-Windows registry entry
- Windows toast notifications for new mail, honoring per-account settings, quiet hours, rule
  mutes and per-rule sounds; clicking a toast opens the message
- Message list density modes (Cozy / Comfortable / Compact), two shipped themes, log viewer page

**Phone integration (Android)**
- A foreground service keeps the sync loop and IMAP IDLE running after the app is left; it starts
  again after the phone boots and after the app is updated
- Each mail check is woken by an exact alarm, so it runs on time while the phone sleeps in Doze
- New mail arrives as an Android notification with the rule's sound; tapping it opens the message
- Attachments and exports are saved to `Download/MyLovelyMail` through MediaStore
- The theme and the calm-motion switch follow the phone's dark theme and animation setting

## Security architecture

- **Credential vault**: account passwords are never stored in plain text. Two at-rest modes:
  - *Device-bound* (default): on Windows encrypted with the Windows user's data protection key
    (DPAPI), on Android with AES-256-GCM under a key generated inside the Android Keystore. Opens
    automatically on that device; a copied `UserData` folder cannot be decrypted elsewhere.
  - *Master password*: AES-256-GCM with a key derived via PBKDF2 (SHA-256, 200k iterations).
    Portable, unlocked through a dedicated lock screen; a wrong password is rejected by the
    GCM tag check. The mode can be switched both ways from Settings, and the vault can be
    locked on demand.
- **Migration bundle**: one file containing settings, accounts, filters and tags plus a
  passphrase-encrypted portable copy of the vault. Cached mail is intentionally excluded and
  re-syncs on the target machine.
- **HTML sanitization**: message HTML never runs scripts; the iframe is sandboxed and remote
  images are blocked by default so tracking pixels do not fire.
- **Tor-only accounts**: the flag is enforced, not advisory. `MailConnections` is the only door to
  a mail server in the app, and for such an account it either attaches a Tor proxy or refuses to
  connect — there is no fall back to a direct connection when Tor is unavailable, and a last-moment
  assertion rejects any client that reaches the connect without a proxy attached. Remote images in
  that account's mail stay blocked whatever the global image setting says, because the WebView
  would fetch them over the ordinary network. Port-probing diagnostics travel through Tor too, or
  are skipped.
- **Debug surface**: the localhost REST API used by automated tests is compiled only into
  DEBUG builds; release builds contain none of it.

## On-disk layout

The deployed app manages its own folder tree next to the launcher:

```
MyLovelyMail\
├─ MyLovelyMail.exe      launcher: starts AppData\MyLovelyMail.exe, forwards arguments
├─ AppData\              application files (replaced by updates)
├─ UserData\             settings, accounts, filters, tags, credential vault,
│                        and local folders — Drafts, Outbox, Sent — which no server has a copy of
├─ UserCache\            synced mail (safe to delete — re-downloads from the server)
└─ AppCache\             app-only re-creatable data (safe to delete)
```

The split is by recoverability, not by who wrote the file: anything a server can hand back lives in
`UserCache`, anything it cannot lives in `UserData`. On Android the same three folders sit inside the
app's private files folder, and the app is excluded from Android's cloud backup.

## Installing

Download either asset from the [latest release](../../releases/latest), unzip it anywhere, and run
`MyLovelyMail.exe` from the folder root. The portable build needs nothing installed; the
framework-dependent one is smaller and needs the .NET 10 desktop runtime.

The executables are digitally signed. To let Windows verify the signature, run `Guven-Kur.cmd` from
`SignatureTrust.zip` once. The programs run without it; only the signature stays unverified.

## Building

Requirements: .NET 10 SDK with the MAUI workload for Windows and Android, Windows 11. `MainProject`
targets `net10.0-android` as well, so restoring any of the heads needs the Android workload.

On Windows Tor is not bundled and is only needed for Tor-only accounts. Install it any way you like —
the tor daemon, the Tor Expert Bundle, or the Tor Browser — and the app finds it on PATH, in its own
folder, or in the usual Tor Browser locations; the `TorExecutablePath` setting points at an unusual
one. Without it, a Tor-only account reports that it has no route rather than connecting directly.
The Android build carries its own tor.

```powershell
git clone https://github.com/muhammetozeski/MyLovelyMail.git
cd MyLovelyMail
dotnet build MyLovelyMail\MyLovelyMail.csproj -f net10.0-windows10.0.19041.0
dotnet build MyLovelyMail\MyLovelyMail.csproj -f net10.0-android -c Release
```

The Android build targets arm64 phones running Android 11 or later.

`Export.ps1` publishes a complete versioned deployment (app + launcher) into a target folder:

```powershell
.\Export.ps1 -DeployRoot "D:\Apps\MyLovelyMail"
```

The solution also contains a Blazor Web head (`MyLovelyMail.Web`) used during development to
preview the shared UI in a browser, and a Roslyn source generator that turns the page list into
navigation constants at compile time.

## Project structure

```
MainProject\           shared library: all UI (Blazor) and services
├─ DataModels\Mail\    accounts, folders, message summaries, filter rules, drafts
├─ Services\Mail\      IMAP/POP3/SMTP engine, rules, compose, notifications, rendering
├─ Services\Tor\       SOCKS5 client, tor process management, endpoint discovery and verification
├─ Storage\            AppPaths, disk-backed MessageStore (RAM keeps summaries only)
├─ Stores\             settings engine, account/filter/tag stores, credential vault
├─ UI\                 pages, components, theme constants (CSS emitted from C#)
├─ Platforms\Windows\  compiled into net10.0 only: DPAPI vault protection
└─ Platforms\Android\  compiled into net10.0-android only: Keystore vault protection, notifications,
                       foreground service, boot receiver, sync alarm, MediaStore downloads, bundled tor
MyLovelyMail\          MAUI head: window, tray, toasts, startup wiring; MauiProgram's per-platform
                       steps live in Platforms\Windows and Platforms\Android
MyLovelyMail.Web\      ASP.NET Core head for browser preview during development
Generators\            incremental source generator for navigation constants
```
