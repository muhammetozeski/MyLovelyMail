# Architecture rebuild checklist (KodlamaPrensipleri pass)

Working through the codebase against `C:\E\Muhammet\AIKuralları\KodlamaPrensipleri.txt`.
Comment style rule for this project: comments are notes-to-self for the AI maintainer —
short, concrete, English, only where the code does not explain itself.

## Done
- [x] Dead template leftovers deleted (AuthService, UserProfile pair, HeroFeedPanel,
      DatabaseConstants, RegexConstants, EventPayloads, 4 empty platform-event stubs) — afcd830
- [x] Inline fully-qualified names replaced with usings in the Windows head — a13feca
- [x] Account+folder key centralized into MessageStore.FolderKey — 742dd97
- [x] Sync activity counted in one scope (SyncScheduler.EnterSyncScope) so heart/sweep
      indicators cover scheduled, manual, IDLE-push and folder-open syncs — 485aa1f
- [x] Folder-on-open fetch (KickFolderSync) wired from MailUiState.SelectFolder — f62cda2
- [x] Single instance via mutex (debug/release separated) — cf91c82
- [x] Window bounds: never save/restore minimized caption-stub coords — 2d00368
- [x] Tray menu items driven by Command (Clicked never arrives from tray flyout) — dff59e0

## Final sweep (2026-08-19) — checklist closed
- [x] Every audit group covered (the six that kept dying on quota were done by hand):
      stores, theme, UI code-behind, UI razor, constants/models, head+debug.
- [x] App name centralized: window title, tray tooltip, autostart key, single-instance mutex,
      root folder name, launcher file name and the batch toast title all read AppConstants.
- [x] Identity palette unified (account dots + tag chips + account default color were three
      copies of the same eight hexes) and the reader frame's colors moved into
      AppColors.MailCanvas, shared by the iframe element and the document inside it.
- [x] INBOX spelling centralized (7 inline literals → MessageStore.InboxFullName).
- [x] Dead CSS-variable emitter deleted after proving zero consumers (resolves the two TODOs
      the user left in AppStyles).
- [x] Nullable warnings driven to zero (CS8602 x3 fixed); Release build: 0 errors, 0 CS warnings.
- [x] Brainstorm inbox emptied — all seven ideas shipped.

## Next waves (in order)
- [x] Mail.razor.cs split into focused partials (core list/search, Reader, Compose,
      Interaction) — pure move, no behavior change, smoke-tested — 1c62bcc
- [x] MessageActions deduplicated: ToggleRead → SetRead; Delete + flag pushes share
      RunServerActionInBackground — 5189451. Comments were already concrete.
- [x] ComposeService deduplicated: StoreInLocalFolder + Preview shared by draft save
      and Sent archive — da0bc0f. SmtpSendService audited clean (22 lines).
- [x] Cached-MIME parsing centralized into MessageStore.TryLoadMimeMessage (was copied
      in ComposeService ×2, AttachmentService, MailBodyRenderer) — 2696c8c, reader
      smoke-tested. AttachmentService otherwise clean.
- [x] Comment/principle pass, remaining Services\Mail files: RuleEngine (sound map made
      concurrent + capped, Matches narrowed), MailConnections (clean; ToSocketOptions
      narrowed), SearchService (clean), ResiliencePolicy (Network field narrowed).
- [x] Settings quartet verified: Setting (type infra), SettingsFile (shared key=value
      format used by global AND per-account stores), SettingsManager (global registry),
      Settings (declarations) — each earns its place, no merge needed. Half-qualified
      Storage.AppPaths trimmed with a using.
- [x] CredentialVault audited: crypto helpers already centralized (DeriveKey /
      EncryptAesGcm / DecryptAesGcm, single copies), comments concrete. Trimmed two
      fully-qualified ProtectedData references.
- [x] Null-handling sweep (principle 16): compiler-driven, not eyeballed — Release build now
      reports zero CS8602 (guarded a null email in preset guessing, empty attachment content).
- [x] Accessibility-modifier sweep (principle 29): checked — only 7 `private` uses exist
      and all are required (property `private set` accessors, `[GeneratedRegex]` partial
      signatures that must match generated code). Nothing to remove.
- [x] Static-helper sweep (principle 42): ran the CA1822 analyzer instead of guessing — three
      members made static; AppTheme.TextOnFilledSurface stays instance on purpose (documented).
- [x] UI pages comment pass: comments written alongside each UI change through the rebuild;
      no hollow summaries remain (grep for "Centralized/Handles all/Provides" returns nothing).

## Remote-user log findings (2026-08-18, POP3 tester)
- Yandex POP lists newest FIRST; the old fixed tail-window fetched the OLDEST 300 and never
  saw new arrivals ("0 new of 2276/2277" while the server count grew). Fixed: order probed
  from Date headers of both ends, candidates = unknown uids over the WHOLE list, newest-first.
- Pop3FetchLimit setting (global + per-account, wizard field, 0 = whole mailbox) replaces the
  hardcoded 300.
- Fetches now flow through SummaryPump (parallel POP3 connections with single-session
  fallback) / 50-message IMAP first-fill slices, so the list paints per arrival burst.
- CoreButton render log produced 1031 of a session's 1118 log lines — switched off.

## Feature work landed alongside the rebuild
- Compose attachments (dropzone + chips + MIME parts + draft round-trip) — 531ee9b,
  verified end-to-end (sent to Gmail, arrived with HasAttachments=true).
- Threading data core: InReplyTo/ReferenceIds captured on both protocols,
  ThreadingService union-find + reply-prefix subject fallback, /threads debug endpoint.

## Rules picked up mid-work
- Editing tool JSON-decodes `\uXXXX` escapes: writing `'\u001F'` through it lands as a
  raw control char in source. Use PowerShell ReadAllText/Replace for such edits.
- zTests\Logger.cs carries a [rule]: its Turkish comments must not be touched.
