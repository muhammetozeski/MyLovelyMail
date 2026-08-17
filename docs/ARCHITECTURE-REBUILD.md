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

## Next waves (in order)
- [ ] Mail.razor.cs (565 lines): split by responsibility — list/reader state, compose,
      bloom tracking, keyboard nav. Candidate: partial classes or extracted services.
      Watch principle "don't split when splitting hurts readability".
- [ ] Comment pass, Services\Mail: rewrite summaries as notes-to-self; kill any
      summary that restates the code (principles 39/40). Files: ComposeService,
      AttachmentService, MessageActions, MailBodyRenderer, SmtpSendService, RuleEngine.
- [ ] Comment pass + principle sweep, Stores: CredentialVault (276 lines — check for
      dup crypto helpers), Setting/Settings/SettingsManager/SettingsFile overlap check:
      four files for settings may violate "merge what can be merged" — verify each earns
      its place, merge if not.
- [ ] Null-handling sweep (principle 16): audit `!` uses and unguarded `?.` chains in
      Services and Stores.
- [ ] Accessibility-modifier sweep (principle 29): drop redundant `private`, make
      helpers static where they capture nothing (principle 42).
- [ ] UI pages comment pass after the structural work settles.

## Rules picked up mid-work
- Editing tool JSON-decodes `\uXXXX` escapes: writing `'\u001F'` through it lands as a
  raw control char in source. Use PowerShell ReadAllText/Replace for such edits.
- zTests\Logger.cs carries a [rule]: its Turkish comments must not be touched.
