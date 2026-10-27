# Working on MyLovelyMail

The MAUI head builds Windows and Android from the same application. `MainProject` owns the shared mail engine and Razor UI. The Web head previews that UI using InteractiveServer; it runs the services on the server so file-backed stores use the same semantics as the native application. `Generators` supplies navigation constants.

## Finding the code to change

| Change | Starting point |
| --- | --- |
| Application startup and native integration | [MauiProgram](../MyLovelyMail/MauiProgram.cs), then the matching `Platforms/Windows` or `Platforms/Android` implementation |
| Message list, search or account selection | [Mail page](../MainProject/UI/Pages/Mail.razor) and [page logic](../MainProject/UI/Pages/Mail.razor.cs) |
| Folder/account presentation | [MailSidebar](../MainProject/UI/Pages/MailSidebar.razor) |
| Reader or compose presentation | [MailReaderPane](../MainProject/UI/Pages/MailReaderPane.razor), with behavior in the `Mail.razor.Reader.cs` and `Mail.razor.Compose.cs` partials |
| Keyboard shortcuts and bulk actions | [Mail interaction](../MainProject/UI/Pages/Mail.razor.Interaction.cs) |
| Colors, sizes or responsive layout | `UI/ThemeConstants`, then the relevant style component rendered by [MailCss](../MainProject/UI/Pages/MailCss.razor) |
| Global or account settings | `Stores/Settings.cs`, `Stores/AccountSettings.cs`, and the corresponding Razor page; [SettingEditorRow](../MainProject/UI/Components/SettingEditorRow.razor) supplies the general editor |
| Folder defaults, overrides and context menu | [FolderSettingsStore](../MainProject/Stores/FolderSettingsStore.cs), [FolderSettingsPanel](../MainProject/UI/Components/FolderSettingsPanel.razor), and `Mail.razor.Folders.cs` using the shared menu host |
| Context menus, side buttons or selection | [ContextMenuService](../MainProject/Services/ContextMenuService.cs), [AppContextMenu](../MainProject/UI/Components/AppContextMenu.razor), [MouseActionService](../MainProject/Services/MouseActionService.cs), and `Mail.razor.Selection.cs` / `Mail.razor.ContextMenus.cs` |
| Mail synchronization | [ImapSyncService](../MainProject/Services/Mail/ImapSyncService.cs); its `Folders` partial discovers folders and its `OnDemand` partial schedules foreground operations |
| Cached messages or local folders | [MessageStore](../MainProject/Storage/MessageStore.cs); the `Index`, `Folders` and `Moves` partials contain those operations |
| Tor | [TorService](../MainProject/Services/Tor/TorService.cs), endpoint verification in its `Verification` partial, and executable discovery in `TorProcess.Discovery.cs` |
| Logging or development endpoints | `Diagnostics/Logger.cs` and `Diagnostics/DebugApi`; only the endpoint implementation is excluded from Release builds |
| Standalone theme editing | Open [Theme Studio](../tools/ThemeStudio/index.html); its HTML, stylesheet, color arithmetic and editing logic are separate from the application |

## Mail page ownership

`Mail` owns selection-dependent caches, drafts, timers and event subscriptions. `MailSidebar` and `MailReaderPane` are presentation sections of that same page. They receive the owning page through `MailPageSection.View`; they do not create their own mail state. An internal page member is visible to these sections without becoming a public application service.

`MailPageSection` forwards event handling to the owning page. This also covers lambdas declared in a section: a click that changes page state must refresh the message list and other sections, not just the clicked section. The regression check exercised this owner repaint using Blazor's renderer.

Reader cache keys include account, folder and message number. Message numbers alone are not unique across folders. Body loads also carry a generation number so a delayed result cannot replace a newer load. Remote-image permission uses the same complete message identity.

The reader document supplies `<base target="_blank">` so ordinary anchors reach BlazorWebView's
[built-in external navigation](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/routing?view=aspnetcore-10.0).
The frame permits popups to escape its sandbox and user-activated top navigation, while scripts
remain disabled. A frame with only `allow-same-origin` blocks links targeting a new or parent
window. The generated target is outside sanitized message content; sender-provided base elements
are still removed. This is document configuration, with no link click listener or custom URL dispatcher.
Renderer checks cover retained hrefs, the generated target and actual frame permissions; the
framework's browser launch behavior follows its documented default.

## Styles and themes

Keep styles in `Css.razor` components and take their values from the existing C# theme constants. Each style component that emits theme-dependent markup inherits `ThemedComponentBase`, which listens for theme and density changes. See [the rendering lessons](LESSONS.md) before changing that inheritance.

`MailCss` renders its style sections in cascade order. Responsive overrides are last. The extraction preserved the original rule text and order; a source comparison also reconstructed the original mail markup from the presentation sections.

## Using the mail engine

Use the operation that owns the complete action rather than rebuilding its steps at every call site:

```csharp
ImapSyncService.KickFolderSync(account, folder.FullName);
ImapSyncService.KickFolderSync(account, folder.FullName, resetCache: true);
ImapSyncService.KickFolderSync(account, folder.FullName, backfillCount: 300);
```

These calls share the per-folder busy guard, cancellation, error tracking and state-change notifications. A cache reset and older-message fetch are mutually exclusive. The cache reset happens only after acquiring the busy guard, so a duplicate request cannot clear an in-flight operation's cache. Awaitable older-message retrieval is available as `BackfillFolderAsync`, with a default batch size.

Use `MessageStore.GetSummaries` for the displayed, date-sorted list. Use `CountSummaries` for counts; its optional predicate supports filtered counts without constructing and sorting the display list.

Keep local-only messages in UserData and server-recoverable messages in UserCache. Storage names, account identifiers and serialized settings are existing data contracts; splitting source files does not change them. Read the storage lessons before changing a folder's owner or cache policy.

## Folder notifications and unread badges

`FolderSettingsStore` owns `UserData/folder-settings.json`, which the migration bundle includes.
Each field resolves independently from an exact account/folder override, a standard-role default,
then the shipped default. Global settings offer only the standard roles in `StandardRoles`;
custom folders are edited in their account. Null fields inherit. An explicit minimum timestamp
enables notifications, the maximum timestamp mutes indefinitely, and other timestamps expire in UTC.
Expiry is read at the notification decision; it needs no background timer or message-flag changes.
An expired explicit override stays unmuted, including Spam, until reset to inheritance.

Mute controls only new-mail notifications in `NotificationService`. Unread-badge visibility is an
independent preference. Trash always resolves to muted with badges disabled, even if saved overrides
request otherwise; account counts use the same badge policy and continue excluding Spam. Incoming
rules' muted destinations are filtered before moves can reassign message numbers, in both receive
protocols. This filters notification candidates without modifying the rules or messages.

Writes use the existing `AtomicFile` mechanism under a store lock and publish live state only after
the write succeeds. Read failures use shipped defaults, log the full exception and prevent edits
from overwriting the unreadable file; a later edit retries that read. Migration import invalidates
the cached preferences. The menu dispatches existing folder operations through the owning `Mail`
page, retaining the clicked folder rather than relying on the current selection.

The store's inheritance, persistence, failure paths, notification presentation, counters and Razor
menu/editor output were checked in an isolated temporary console/Blazor renderer harness. This is
service and renderer verification, not a native-device interaction test.

## Shared input and message identity

`ContextMenuService` owns one menu, rendered by `AppContextMenu` in the layout outside the animated
page content. Entries capture their owner and targets. Execution passes through that owner's
Blazor event receiver so changing a menu does not repaint only the popup. Route changes dismiss
captured targets. Account menus use the same `MailMenuItems.ForAccount` factory in Mail and Settings.

`MailMessageIdentity` is the common account/folder/message key for selection, reader caches and
mouse payloads. `MailUiState.SelectMessages` owns selection updates; a conversation row expands
the existing `ThreadingService` group's members. Shift selection uses visible row order. Right-click
inside a selection preserves it; an outside row replaces it. Non-filing commands retain selection.
Blank workspace context menus never infer a message from focus, the reader or selection; they
offer workspace actions and leave selection unchanged. `WorkspaceTargets` is only a side-button fallback.
`MouseActionService` provides the shared input failure boundary and status; `MessageActions.Execute`
resolves complete keys and batches each folder using the existing storage and protocol operations.

DOM button 4 means Mouse 5 (front/read); DOM button 3 means Mouse 4 (Trash). `mouse-buttons.js`
uses declared bindings plus editing/control/global fallbacks. Editable fields retain native context
menus, passwords are never copied, and Ctrl+A is intercepted only in the mail list. The existing
reader-frame watcher attaches the same input router inside readable mail documents. The opaque
signature preview retains its sandbox and forwards trusted input through a per-instance capability
removed before user code runs; the parent verifies the owned frame and discards arbitrary commands.
These dispatch paths and viewport/scale geometry were checked with an isolated JavaScript harness.

Trash shortcuts explicitly request `MoveToTrash` and never inherit permanent deletion. A writable
server move retains its source until confirmed, deduplicates queued requests and hides pending rows;
failure releases the pending state and makes the retained row visible again. `MoveCachedMessage`
handles local and confirmed server cache moves in one place. It copies metadata before changing the
destination number, writes the destination body/index, then removes the source. Sharing the same
mutable summary between both folders can make cross-folder search dictionaries fail during a change
notification. Local number collisions and full-body preservation were checked in isolated storage tests.
POP3's known incoming-mail numbers include filed local mail so Trash does not turn it into a new arrival.

`SyncScheduler.SyncNowAsync(accountId: id)` reuses the existing protocol/health path for a manual
account check; global IDLE refresh, Outbox flushing and maintenance remain in the global pass.

## Building and checking changes

The cleanup was compiled with the installed .NET SDK 10.0.400. Restore dependencies for the intended targets before using `--no-restore`. A Windows-only restore uses `-p:SkipAndroidTargets=true`; a later Android or whole-solution build needs a restore that includes Android again.

```powershell
dotnet build MyLovelyMail/MyLovelyMail.csproj -f net10.0-windows10.0.19041.0 -p:SkipAndroidTargets=true
dotnet build MyLovelyMail/MyLovelyMail.csproj -f net10.0-android -c Release
dotnet build MyLovelyMail.Web/MyLovelyMail.Web.csproj -p:SkipAndroidTargets=true
```

`Publish.ps1` produces local outputs for the supported targets and stops at the first failed publish. Android packaging uses arm64 because that is the architecture of the bundled Tor executable. `Export.ps1` and `Release.ps1` remain the deployment and release entry points described in the project overview.

Development endpoints consume the same application services as the UI. `DebugApi.cs` owns the listener, request checks and dispatcher; the sibling files group routes by application, settings, connections, folders, storage, compose, messages and rules. Existing paths and HTTP methods were preserved. Do not put test helpers into lower-level production classes just to support an endpoint.

The Android minimum is version 11. `MailSyncService.OnStartCommand` uses the `specialUse` foreground service type only from Android 14 onward and the earlier foreground-service overload below that version. This follows [Android's version-dependent service-type guidance](https://developer.android.com/develop/background-work/services/fgs/launch); compiling the Android target checks the platform guard. This is source and compiler validation, not a phone test.
