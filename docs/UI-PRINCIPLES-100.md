# 100 UI Element Behavior Principles & Proactive Architecture Extensions

## Part I — 100 Concrete UI Element Behavior Principles

### A. Status Toasts, Info Banners & Notifications (1–10)
1. **Single-Slot Replacement**: Transient status/info banners must occupy a single visual slot and replace previous text in place instead of stacking multiple notifications on top of each other.
2. **Automatic Timed Dismissal**: Every non-blocking status toast (`MouseActionService.ReportStatus`) must auto-close after a predictable interval (~4 seconds) using a versioned timer so an older timer never closes a newer message.
3. **Manual Dismiss Affordance**: Any visible status toast must allow immediate dismissal on click without triggering underlying elements.
4. **No Layout Shift on Toast**: Transient status notifications must float or occupy a reserved bar so their appearance/disappearance never shifts message rows or buttons vertically.
5. **Actionable Error Persistence**: Error banners requiring user intervention (such as held Outbox retries or send-guard warnings) remain visible until resolved or explicitly dismissed, unlike informational toasts.
6. **Progress vs. Completion Separation**: In-flight progress indicators (`Syncing…`) must transition cleanly into completion/idle states without leaving stale status strings on screen.
7. **Undo Window Clarity**: Delayed destructive or outbound actions (such as Undo Send) must display a live countdown and a single-click `Undo` button right in the active pane.
8. **Silent Background Maintenance**: Periodic background syncs that find zero changes must not emit status toasts that distract the user.
9. **Contextual Consequence Reporting**: Operations that export or save files (`ExportFolderMailbox`, `.eml`, `.html`) must report the exact file path and reveal the saved file in Windows Explorer.
10. **Screen-Reader Politeness**: Live status regions that update frequently (such as Tor bootstrap progress) must use `aria-live="polite"` rather than `assertive`.

### B. Modals, Dialogs & Popovers (11–22)
11. **Zero Native Browser Dialogs**: Never use `window.confirm()`, `window.alert()`, or `window.prompt()`; every confirmation and prompt must render through the application's themed `ModalService`.
12. **Strict Modal Inheritance Hierarchy**: Every modal and popover (`CompactModal`, `DialogModal`, `WorkspaceModal`, `WideModal`) must inherit from `ModalBase` and render through `CoreModal` (`AppModalHost`).
13. **Opaque Surface Readability**: Floating modals, popovers, and context menus must use an opaque theme-matched surface (`AppColors.TooltipBackground`) so text underneath never bleeds through and reduces legibility.
14. **Click-Outside Dismissal**: Clicking the modal backdrop outside `.core-modal-card` must immediately close the active modal (`CloseOnBackdrop = true`).
15. **Escape Key Dismissal**: Pressing `Escape` anywhere while a modal or context menu is open must close the topmost overlay before affecting selection or reader state.
16. **Smooth Entrance Animation**: Modals must animate in with a short, crisp scale-and-translate transition (`cubic-bezier(0.16, 1, 0.3, 1)`) that respects `CalmMotion`.
17. **Compact Modal Sizing for Quick Pickers**: Lightweight pickers (`Move`, `Snooze`, `Star/Important/Tags`, `Save`) must use `CompactModal` (`380px`) rather than full-screen dialogs.
18. **Modal History Back-Stack**: When a secondary modal opens from a primary modal, `ModalService` must preserve the stack and offer a Back button.
19. **Destructive Confirmation Emphasis**: Confirmation modals for destructive actions (`Empty Trash`, `Delete Folder`, `Delete Account`) must use `DangerButton` for the confirm action and focus-safe `Cancel` by default.
20. **Single Overlay Exclusivity**: Opening a modal must close any open context menu, and vice versa, so two floating layers never compete for input.
21. **Scrollable Modal Body with Fixed Header**: Long modal content (`MessageSourceModal`, `ShortcutHelpModal`) must scroll inside `.core-modal-body` while keeping the modal header and close button pinned at the top.
22. **Immediate State Reflection**: Toggling Star, Importance, or Tags inside `MessageMarkersModal` must update the underlying message and modal chips immediately without requiring a separate "Apply" button.

### C. Scrollbars & Scroll Containers (23–32)
23. **Centralized Scrollbar Style Inheritance**: All scrollbar rules must originate from a single C# style builder (`AppStyles.BuildScrollbarStyles()`) and apply uniformly across `.app-main`, `.mail-sidebar`, `.mail-list-rows`, `.mail-reader`, `.core-modal-body`, and `.context-menu`.
24. **Transparent Track by Default**: Scrollbar tracks must be completely transparent (`background: transparent`) in their resting state so they never draw heavy vertical bars across glass surfaces.
25. **Subtle Track on Container Hover**: Hovering a scroll container or its scrollbar track reveals a soft translucent track guide (`ResolvedScrollbarTrackHoverColor()`).
26. **Single Flat Accent Thumb**: Scrollbar thumbs must use a single flat high-contrast accent color (`#4ADE80` bright light green by default) rather than multi-stop gradients.
27. **Slender Default Thickness**: Scrollbars default to a slender `6px` width/height, customizable in Settings (`Fine-Tuning`).
28. **No Nested Vertical Scrollbars in Reader**: The email reader iframe (`.mail-reader-frame`) must never show its own inner vertical scrollbar; it expands to `110%` of document height (`overflow-y: hidden`) so only the outer column scrolls.
29. **Dynamic Iframe Height Observation**: When images or quoted blocks expand inside an email body, a `ResizeObserver` and `MutationObserver` must resize the iframe automatically without clipping or spawning an inner scrollbar.
30. **Wheel Event Forwarding**: Mouse wheel events over the seamless reader iframe must forward smoothly to the parent scroll container.
31. **Independent Column Scrollports**: On desktop, Column 1 (Sidebar), Column 2 (Message List), and Column 3 (Reader) each maintain independent scroll positions.
32. **Horizontal Overflow Protection**: Wide tables or preformatted blocks inside an email or source view may scroll horizontally inside their box without forcing the entire column wider.

### D. Three-Column Layout & Resizable Splitters (33–42)
33. **Proportional Percentage Columns**: The three main columns (`MailSidebar`, `MailList`, `MailReader`) size by percentage (`20% / 32% / 48%` defaults) rather than rigid pixel widths.
34. **Double-Precision Storage**: Column width percentages persist in `double` precision (`Setting<double>` using `"R"` round-trip invariant formatting) so sub-percent adjustments never truncate.
35. **Visual Splitter Alignment**: Thin splitter lines sit directly alongside the column scrollbars and share the active scrollbar accent color (`AppStyles.ResolvedScrollbarColorHex()`).
36. **Magnetic $\pm 3\%$ Default Snap**: Dragging a column splitter within $\pm 3.0$ percentage points of its default (`20.0%` or `32.0%`) snaps cleanly to the default; dragging beyond $\pm 3.0$ points breaks free smoothly.
37. **Double-Click Splitter Reset**: Double-clicking either column splitter immediately resets all three columns to `20% / 32% / 48%`.
38. **Escape Cancels Splitter Drag**: Pressing `Escape` while dragging a column splitter restores the pre-drag column widths immediately.
39. **Minimum Column Floors**: Splitter dragging clamps each column (`Sidebar >= 12%`, `List >= 18%`, `Reader >= 24%`) so no pane can be crushed to zero width.
40. **Fine-Tuning Settings Synchronization**: Dragging a splitter updates the `Settings` store on mouse release, and editing percentages in Settings (`🎛️ Fine-tuning`) updates the live grid immediately.
41. **Zero Iframe Pointer Capture During Resize**: While dragging a column splitter, iframes are temporarily pointer-shielded so the drag never stalls when crossing over the mail body.
42. **Responsive Breakpoint Collapse**: Below desktop breakpoints, custom desktop column percentages yield cleanly to the responsive stacked layout.

### E. Sidebar Tree, Accounts & Folders (43–54)
43. **Uncluttered Folder Rows at Rest**: Per-folder action buttons (`+`, `↑`, `↓`, `✕`) stay hidden (`display: none`) until the row is hovered or focused, leaving the full row width for the folder name and unread badge.
44. **Left-Gutter Status Icons**: Persistent status indicators such as the muted-folder bell (`🔕`) sit in the left indentation gutter before the folder icon rather than squeezing the folder title on the right.
45. **Collapsible Hierarchy for All Parent Nodes**: Both account folders (`AccountFolderData`) and parent mail folders (`MailFolderData`) provide a disclosure caret (`▾`/`▸`) that persists `IsExpanded` across restarts.
46. **Preserved Folder SortOrder Across Sync**: Server folder synchronization (`ImapSyncService.Folders`) preserves user-defined `SortOrder` and `IsExpanded` state instead of resetting them on every IMAP refresh.
47. **In-Memory Folder Metadata Cache**: Folder lists are served from an in-memory cache (`MessageStore.Folders`) invalidated only on actual mutation, avoiding repeated disk reads on every UI render.
48. **Universal Sidebar Drag-and-Drop**: Account folders, accounts, and mail folders all support drag-and-drop reordering and nesting, while messages can be dragged from the list onto any selectable folder.
49. **Active Drag Status Banner**: While dragging any item in the workspace, a live banner indicates what is being dragged and reminds the user that `Escape` cancels the drag.
50. **Clear Drop Target Highlight**: Hovering a valid drop target during drag highlights the target border and background with the primary accent glow.
51. **Escape Cancels Any Drag Before Drop**: Pressing `Escape` before releasing the mouse button aborts the active drag operation (`mailCancelActiveDrag`) and suppresses the subsequent `drop` event.
52. **One-Click Empty Trash / Empty Spam**: When `Trash` or `Spam` is selected and non-empty, a dedicated `🗑️ Empty Trash` / `🧹 Empty Spam` action is offered in the sidebar and folder context menu with confirmation.
53. **Fast Mail Quick-Creation**: Anonymous/disposable Fast Mail accounts can be created in one click and recovered from the encrypted deleted-accounts history.
54. **Account Accent Identity**: Each account carries a distinct high-contrast accent color (`ColorHex`) reflected on its rail pill, folder indicator, and compose `From` chip.

### F. Message List & Selection Decoupling (55–66)
55. **Strict Column 2 / Column 3 Selection Decoupling**: Right-clicking inside Column 3 (the Reader pane) or opening a context menu on a single row without active multi-selection must never auto-tick checkboxes in Column 2 or pop open `.mail-bulk-bar`.
56. **Compact Toolbar Spacing**: Search, filter, and selection controls at the top of Column 2 use tight `4px` gaps with zero wasted vertical whitespace above or below the control strip.
57. **Direct Keyboard Shortcut Button**: The top of Column 2 provides a compact `⌨️` emoji button that opens `ShortcutHelpModal` directly in one click.
58. **Virtualized Row Rendering**: Large folders (17,000+ messages) render through `<Virtualize>` with fixed row heights per density mode (`Cozy`, `Comfortable`, `Compact`) so DOM node count stays tiny.
59. **$O(1)$ Sender Count Lookup**: Per-row sender message counts (`×N`) use a precomputed per-folder dictionary (`MessageStore.GetSenderMessageCount`) rather than scanning the summary list per row.
60. **Coalesced Sync UI Updates**: Incoming message summaries during IMAP sync are coalesced (`SummaryPump` 90ms window) so the index file and UI list update in batches rather than hundreds of times per second.
61. **Shift-Click Range Selection**: `Shift+Click` and `Shift+Up/Down` select contiguous visible rows between `SelectionAnchor` and the target row.
62. **Selection Predicate Menu**: The `Select ▾` menu offers instant selection by state (`Unread`, `Read`, `Starred`, `Important`, `With attachments`, `Tagged`, `Muted`, `Snoozed`, `Invert`).
63. **Conversation Thread Grouping**: When thread grouping is enabled, replies collapse under the newest message with a thread count pill and participant list.
64. **New-Arrival Bloom**: Newly arrived unread messages in the open folder bloom once and then join the seen set without looping animations.
65. **Outbox Hold Visibility**: Messages held after repeated SMTP failures surface a top retry bar even when the user is not viewing the `Outbox` folder.
66. **Search Rescue Suggestions**: When a search query returns zero matches or contains a typo in an operator, `SearchRescueService` offers one-click corrected queries.

### G. Reader Pane Header & Action Consolidation (67–78)
67. **Full-Width Inline Reader Header**: `<BackButton>`, sender avatar, subject, and `←`/`→` list navigation share the top horizontal row so every row underneath uses 100% of Column 3's width with zero empty space below the avatar.
68. **Compact Subject Presentation**: The message subject sits inline on the top header row with tight line-height and tooltip full-text on hover, eliminating wasted vertical padding underneath.
69. **Right-Aligned Recipient (`to ...`) on Date Row**: The `to ...` recipient summary sits on the right side (`margin-left: auto`) of the sender/date metadata line rather than consuming its own mostly empty row.
70. **Single Read/Unread Toggle Button**: The reader toolbar renders a single button that switches dynamically between `✅ Mark read` and `📩 Mark unread` based on `open.IsUnread`.
71. **Unified `↩️ Reply ▾` Menu Button**: `Reply`, `Reply all`, `Forward`, and `Forward as attachment` are grouped under a single `↩️ Reply ▾` menu button.
72. **Unified `⭐ Labels` Modal Button**: Star, Important, and Tags are consolidated into a single button that opens the animated `MessageMarkersModal`.
73. **Unified `💾 Save` Modal Button with Save As & Explorer Reveal**: Saving `.eml` or `.html` opens `MessageSaveModal`, defaults to the user's `Downloads` folder (`C:\E\!\Downloads`), lets the user pick a custom path via the native Windows Save As dialog, and opens Explorer with the saved file selected.
74. **Shared Action Inheritance (`ReaderActionDescriptor`)**: Toolbar buttons and `Message actions ▾` menu items are generated from a single shared definition (`MailMenuItems.BuildUnifiedReaderActions`) so no action is coded twice.
75. **Middle-Click / Right-Click Action Pinning**: Any action inside `Message actions ▾` can be middle-clicked or right-clicked to pin/unpin it as a favorite button right next to `Message actions ▾` on the reader toolbar.
76. **Permanent Delete in Trash**: Clicking `🗑️ Delete forever` while viewing a message in `Trash` permanently deletes the message from server and local cache (`DeleteBehavior.DeletePermanently`).
77. **Inline Text Scale Controls (`A-` / `100%` / `A+`)**: Reader zoom controls sit compactly at the right end of the reader toolbar and respond to `Ctrl+-`, `Ctrl+=`, and `Ctrl+0`.
78. **Privacy-First Remote Image Blocking**: Remote images are blocked by default with a one-click `Show images once` banner on regular accounts, and permanently blocked on `TorOnly` accounts so WebView never leaks clearnet requests.

### H. Context Menus, Keyboard & Mouse Ergonomics (79–90)
79. **Viewport-Clamped Context Menus**: Context menus measure and clamp their position so submenus never overflow off the right or bottom edge of the window.
80. **Submenu Hover & Click Support**: Context menu items with `Children` open their submenu smoothly on hover or click.
81. **Mouse 4 / Mouse 5 Side-Button Bindings**: Back (`Mouse 4` -> Trash/Back) and Forward (`Mouse 5` -> Read/Menu) side buttons work consistently across rows, reader panes, and iframes.
82. **Native Context Menu in Editable Inputs**: `<input>`, `<textarea>`, and `[contenteditable]` elements preserve the native OS copy/paste/spellcheck menu unless explicitly overridden.
83. **Password Field Copy Guard**: Password inputs (`type="password"`) never allow side-button or shortcut copying of their value.
84. **Single-Key Triage Shortcuts**: `J`/`K` (focus), `Enter` (open), `U` (unread), `S` (star), `I` (important), `M` (mute), `T` (labels modal), `X` (select), `N`/`P` (thread step), and `?` (shortcuts modal) work whenever focus is outside an input box.
85. **Account Switching Shortcuts (`Ctrl+1..9`)**: `Ctrl+1` through `Ctrl+9` switch accounts in the exact visual order of the sidebar rail.
86. **Authentication Spoofing Warnings Only**: The reader only displays `⚠️` badges for failed/missing SPF, DKIM, or DMARC (`MessageAuthService`) and never shows a green checkmark for trivially forgeable headers.
87. **Tor-Safe Unsubscribe Chips**: `List-Unsubscribe` HTTP links are blocked on `TorOnly` accounts (`🛡️ Web unsubscribe blocked (Tor)`), while `mailto:` unsubscribe links route safely through the account's Tor SMTP tunnel.
88. **Rule Trace Transparency**: Messages modified by incoming filter rules display a collapsible `🧭` rule trace strip linking directly to `FilterRules`.
89. **Correspondent Profile Sheet**: Clicking a sender's avatar or history badge opens the `PersonProfileService` sheet inside Column 3 with message statistics, folder breakdown, tags, and quick search filters.
90. **Attachment Quick-Save & Save-All**: Attachment chips show file type icons and human sizes, save to `Downloads` on click, and offer `📦 Save all` when multiple attachments exist.

### I. Performance, CPU Discipline & Fine-Tuning (91–100)
91. **Zero Continuous Background CSS Filter Animations**: Never run infinite `filter: hue-rotate(...)` or `backdrop-filter` keyframe animations on full-screen pseudo-elements (`body::before`), which consume 10–40% GPU/CPU while completely idle.
92. **Skip Redundant Disk Writes During Sync**: Folder discovery and message sync compare incoming counts/flags against cached values and skip disk I/O and UI change events when nothing changed.
93. **No Synchronous Whole-Mailbox Scans on Render**: UI components must never iterate all folders or all messages on the render thread; cross-folder totals and sender counts use cached dictionaries.
94. **Dedicated Fine-Tuning Settings Card**: Power-user visual metrics (scrollbar width, scrollbar/splitter color, three-column width percentages) live in a dedicated `🎛️ Fine-tuning` card in Settings.
95. **Explicit Default Values Shown in UI**: Every fine-tuning control displays its shipped default value (`Default: 6 px`, `Default: #4ADE80`, `Defaults: 20% / 32% / 48%`) right in its description.
96. **Per-Setting and Section Reset Buttons**: Each fine-tuning row offers an individual `↺ Default` button (disabled when already at default) plus a master `↺ Reset all fine-tuning to defaults` button.
97. **Theme Studio & Live Event Reactivity**: Changing any theme or fine-tuning setting triggers `OnStyleChanged` so `ThemedComponentBase` styles update immediately without restarting the app.
98. **WCAG Contrast Ink Selection**: Dynamic badges (account chips, tag chips) compute foreground ink via `AppColors.InkOn(hex)` to guarantee readable contrast on any user-chosen color.
99. **Off-Thread Heavy Diagnostics**: Full-cache integrity scans (`StoreCheckService.Run`) and network verifications (`TorService.VerifyAsync`) run off the UI thread behind a `CoreSpinner`.
100. **Atomic Persistence Under Lock**: All settings, folder trees, and message index updates write to a temporary file and atomically replace the target file under a lock so a crash never corrupts user state.

---

## Part II — Proactive Prompt Continuation & Implemented Extensions

Following the user's instruction (*"ve bu sana yazdığım uzun prompt var ya. bunu sen devam ettir tamam mı kendin. proaktif ol yani. daha iyisini yapmak için çabala ve kendi yazdığın ek prompt'u da uygula"*), we extended the specification with the following proactive architectural improvements and implemented them directly:

1. **Double-Click Column Splitter Reset & Escape Mid-Resize Cancel**:
   - In addition to dragging the thin splitters with $\pm 3\%$ magnetic snap, double-clicking any column splitter immediately restores the `20% / 32% / 48%` default layout, and pressing `Escape` mid-drag cancels the resize and restores the pre-drag widths.
2. **One-Click `Empty Trash` & `Empty Spam` with Confirmation Modal**:
   - In addition to making `Delete` inside `Trash` permanently delete selected messages, we added `MessageActions.EmptyFolder` and surfaced `🗑️ Empty Trash` and `🧹 Empty Spam` both in the sidebar action strip (when viewing Trash/Spam) and in the folder right-click context menu, guarded by `ModalService.ConfirmAsync`.
3. **Interactive Toolbar Pinning via Both Middle-Click and Right-Click — On Both Menu Items and Pinned Toolbar Buttons**:
   - Users can pin/unpin favorite actions (`Archive`, `Move`, `Snooze`, `Mute`, `Spam`, `Source`, `Copy Subject`, `Copy Sender`, etc.) not only from inside `Message actions ▾` (with a live `📌` badge), but can also middle-click or right-click any pinned button directly on the reader toolbar to unpin it immediately.
4. **Automatic Windows Explorer Reveal Across All Exports**:
   - Both single-message exports (`.eml` / `.html` via `MessageSaveModal`) and whole-folder `.mbox` exports (`ExportFolderMailbox`) now reveal and select the written file in Windows Explorer (`explorer.exe /select,"path"`).
5. **17k-Message Sync Burst Coalescing & $O(1)$ Sender Count Indexing**:
   - Beyond removing the idle CSS `hue-rotate` CPU loop, we eliminated the two largest sync/render CPU bottlenecks on 17,000-message mailboxes: `SummaryPump` now coalesces burst arrivals into 90ms batches before rewriting `index.jsonl`, and `MessageStore.Index` maintains an $O(1)$ `SenderCounts` dictionary per folder so rendering virtualized rows never scans the 17,000-message list.
