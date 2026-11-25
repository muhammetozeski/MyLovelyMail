namespace MyLovelyMail.MainProject.UI.Pages;

/// <summary>
/// Compile-time CSS class name constants for the Mail workspace and its child components.
/// Never write raw CSS class strings in .razor or *Css.razor files; reference these constants instead.
/// </summary>
public static class MailCssClasses
{
    public const string Screen = "mail-screen";
    public const string ScreenPaneOpen = "mail-screen--pane-open";
    public const string ScreenResizing = "mail-screen--resizing";

    public const string ColumnSplitter = "mail-column-splitter";
    public const string ColumnSplitterDragging = "mail-column-splitter--dragging";
    public const string ColumnSplitterGrip = "mail-column-splitter-grip";

    public const string Sidebar = "mail-sidebar";
    public const string SidebarTopBar = "mail-sidebar-top-bar";
    public const string SidebarWorkspaceDragging = "mail-sidebar--workspace-dragging";
    public const string SidebarNav = "mail-sidebar-nav";
    public const string SidebarStatus = "mail-sidebar-status";
    public const string Logo = "mail-logo";
    public const string LogoText = "mail-logo-text";
    public const string DragEscapeBanner = "mail-drag-escape-banner";
    public const string WelcomeCard = "mail-welcome-card";
    public const string WelcomeIcon = "mail-welcome-icon";
    public const string WelcomeTitle = "mail-welcome-title";
    public const string WelcomeText = "mail-welcome-text";

    public const string AccountFolderRow = "mail-account-folder-row";
    public const string AccountFolderToggle = "mail-account-folder-toggle";
    public const string AccountFolderChevron = "mail-account-folder-chevron";
    public const string AccountFolderIcon = "mail-account-folder-icon";
    public const string AccountFolderName = "mail-account-folder-name";
    public const string AccountFolderAction = "mail-account-folder-action";

    public const string Account = "mail-account";
    public const string AccountActive = "mail-account--active";
    public const string AccountPaused = "mail-account--paused";
    public const string AccountDot = "mail-account-dot";
    public const string AccountFailing = "mail-account-failing";
    public const string AccountPausedIcon = "mail-account-paused";
    public const string AccountOrdinal = "mail-account-ordinal";
    public const string AccountName = "mail-account-name";
    public const string AccountUnread = "mail-account-unread";

    public const string FolderRow = "mail-folder-row";
    public const string FolderGutter = "mail-folder-gutter";
    public const string FolderChevron = "mail-folder-chevron";
    public const string FolderMutedGutter = "mail-folder-muted-gutter";
    public const string Folder = "mail-folder";
    public const string FolderActive = "mail-folder--active";
    public const string FolderIcon = "mail-folder-icon";
    public const string FolderName = "mail-folder-name";
    public const string FolderHoverActions = "mail-folder-hover-actions";
    public const string FolderAction = "mail-folder-action";
    public const string FolderQuickBtn = "mail-folder-action";
    public const string NewFolderBtn = "mail-folder-action mail-account-folder-action";
    public const string FolderMenuTrigger = "mail-folder-menu-trigger";
    public const string DropTarget = "mail-drop-target";
    public const string DropBefore = "mail-drop-before";
    public const string DropAfter = "mail-drop-after";

    public const string List = "mail-list";
    public const string Search = "mail-search";
    public const string SelectionTools = "mail-selection-tools";
    public const string SyncLine = "mail-sync-line";
    public const string SyncLineRunner = "mail-sync-line-runner";
    public const string SearchFoldRow = "mail-search-fold-row";
    public const string SearchTools = "mail-search-tools";

    public const string SearchToggle = "mail-search-toggle";
    public const string SearchToggleOn = "mail-search-toggle--on";
    public const string SearchToggleDanger = "mail-search-toggle--danger";
    public const string SearchToggleIcon = "mail-search-toggle--icon";
    public const string SearchTogglePinned = "mail-search-toggle--pinned";
    public const string SearchToggleCompact = "mail-search-toggle--compact";

    public const string ActionBtn = "mail-action-btn";
    public const string ActionBtnActive = "mail-action-btn--active";
    public const string ActionBtnDanger = "mail-action-btn--danger";

    public const string SavedSearches = "mail-saved-searches";
    public const string SavedChip = "mail-saved-chip";
    public const string SavedRun = "mail-saved-run";
    public const string SavedRemove = "mail-saved-remove";

    public const string BulkBar = "mail-bulk-bar";
    public const string BulkCount = "mail-bulk-count";
    public const string BulkMove = "mail-bulk-move";
    public const string BulkTagNew = "mail-bulk-tag-new";
    public const string MovePopover = "mail-move-popover";
    public const string MoveDivider = "mail-move-divider";
    public const string MoveOption = "mail-move-option";
    public const string MoveFilter = "mail-move-filter";

    public const string Rows = "mail-rows";
    public const string RowShell = "mail-row-shell";
    public const string Row = "mail-row";
    public const string RowUnread = "mail-row--unread";
    public const string RowActive = "mail-row--active";
    public const string RowFocused = "mail-row--focused";
    public const string RowBloom = "mail-row--bloom";
    public const string RowCheck = "mail-row-check";
    public const string RowCheckOn = "mail-row-check--on";
    public const string RowAvatar = "mail-row-avatar";
    public const string RowAvatarUnread = "mail-row-avatar--unread";
    public const string RowDot = "mail-row-dot";
    public const string RowMain = "mail-row-main";
    public const string RowTop = "mail-row-top";
    public const string RowFrom = "mail-row-from";
    public const string RowThreadCount = "mail-row-thread-count";
    public const string RowDate = "mail-row-date";
    public const string RowSubject = "mail-row-subject";
    public const string RowFlag = "mail-row-flag";
    public const string RowFlagMuted = "mail-row-flag--muted";
    public const string RowPreview = "mail-row-preview";
    public const string RowOutbox = "mail-row-outbox";
    public const string RowFolder = "mail-row-folder";
    public const string RowAttach = "mail-row-attach";
    public const string RowSnoozed = "mail-row-snoozed";
    public const string RowQuickActions = "mail-row-quick-actions";
    public const string RowQuickBtn = "mail-row-quick-btn";
    public const string RowQuickBtnDanger = "mail-row-quick-btn--danger";

    public const string TagChip = "mail-tag-chip";
    public const string TagChipAction = "mail-tag-chip--action";
    public const string TagChipOff = "mail-tag-chip--off";
    public const string TagDot = "mail-tag-dot";
    public const string TagPicker = "mail-tag-picker";
    public const string TagInput = "mail-tag-input";
    public const string TagNew = "mail-tag-new";

    public const string Backfill = "mail-backfill";
    public const string BackfillText = "mail-backfill-text";
    public const string BackfillButton = "mail-backfill-button";
    public const string BackfillInput = "mail-backfill-input";

    public const string Reader = "mail-reader";
    public const string ReaderContent = "mail-reader-content";
    public const string ReaderHeaderCard = "mail-reader-header-card";
    public const string ReaderTopRow = "mail-reader-top-row";
    public const string ReaderAvatar = "mail-reader-avatar";
    public const string ReaderSubject = "mail-reader-subject";
    public const string ReaderListNav = "mail-reader-list-nav";
    public const string ReaderMetaRow = "mail-reader-meta-row";
    public const string ReaderMetaLeft = "mail-reader-meta-left";
    public const string ReaderFrom = "mail-reader-from";
    public const string ReaderDate = "mail-reader-date";
    public const string ReaderTo = "mail-reader-to";
    public const string ReaderToAddress = "mail-reader-to-addr";
    public const string ReaderBadgesRow = "mail-reader-badges-row";
    public const string ReaderHistory = "mail-reader-history";
    public const string ReaderActions = "mail-reader-actions";
    public const string ReaderBody = "mail-reader-body";
    public const string ReaderFrame = "mail-reader-frame";
    public const string ReaderPreview = "mail-reader-preview";
    public const string ReaderNoBody = "mail-reader-nobody";
    public const string BodyLoading = "mail-body-loading";

    public const string ReaderThread = "mail-reader-thread";
    public const string ReaderThreadNav = "mail-reader-thread-nav";
    public const string ReaderThreadCount = "mail-reader-thread-count";
    public const string ThreadStep = "mail-thread-step";
    public const string ThreadLine = "mail-thread-line";
    public const string ThreadLineOpen = "mail-thread-line--open";
    public const string ThreadLineDate = "mail-thread-line-date";
    public const string ThreadLineSender = "mail-thread-line-sender";
    public const string ThreadLineUnread = "mail-thread-line-unread";

    public const string Empty = "mail-empty";
    public const string EmptyIcon = "mail-empty-icon";
    public const string EmptyText = "mail-empty-text";
    public const string EmptyDetail = "mail-empty-detail";
    public const string InboxZero = "mail-inbox-zero";
    public const string InboxZeroHearts = "mail-inbox-zero-hearts";
    public const string InboxZeroHeart = "mail-inbox-zero-heart";
    public const string InboxZeroHeadline = "mail-inbox-zero-headline";
    public const string InboxZeroTitle = "mail-inbox-zero-title";
    public const string Rescue = "mail-rescue";

    public const string SendGuard = "mail-send-guard";
    public const string FromChip = "mail-from-chip";
    public const string FromChipOn = "mail-from-chip--on";
    public const string TextScale = "mail-text-scale";
    public const string TextScaleStep = "mail-text-scale-step";

    public const string PaneTabs = "mail-pane-tabs";
    public const string PaneTab = "mail-pane-tab";
    public const string PaneTabOn = "mail-pane-tab--on";

    public const string PersonSheet = "mail-person-sheet";
    public const string PersonHead = "mail-person-head";
    public const string PersonName = "mail-person-name";
    public const string PersonClose = "mail-person-close";
    public const string PersonAddress = "mail-person-address";
    public const string PersonStats = "mail-person-stats";
    public const string PersonFolder = "mail-person-folder";
    public const string PersonTags = "mail-person-tags";
    public const string PersonActions = "mail-person-actions";

    public const string RuleTrace = "mail-rule-trace";
    public const string RuleStrip = "mail-rule-strip";
    public const string RuleSummary = "mail-rule-summary";
    public const string RuleRow = "mail-rule-row";
    public const string RuleLead = "mail-rule-lead";
    public const string RuleName = "mail-rule-name";
    public const string RuleCount = "mail-rule-count";
    public const string RuleCaret = "mail-rule-caret";
    public const string RuleRows = "mail-rule-rows";
    public const string AuthChip = "mail-auth-chip";

    public const string Unsubscribe = "mail-unsubscribe";
    public const string UnsubscribeChip = "mail-unsubscribe-chip";
    public const string UnsubscribeChipBlocked = "mail-unsubscribe-chip--blocked";

    public const string ComposeBtn = "mail-compose-button";
    public const string ComposeCard = "mail-compose-card";
    public const string ComposeFields = "mail-compose-fields";
    public const string ComposeSaved = "mail-compose-saved";
    public const string ComposeFrom = "mail-compose-from";
    public const string ComposeBody = "mail-compose-body";
    public const string ComposeDropzone = "mail-compose-dropzone";
    public const string ComposeFileInput = "mail-compose-file-input";
    public const string ComposeAttachments = "mail-compose-attachments";
    public const string ComposeAttachmentChip = "mail-compose-attachment-chip";
    public const string ComposeAttachmentRemove = "mail-compose-attachment-remove";
    public const string ComposeError = "mail-compose-error";
    public const string ComposeActions = "mail-compose-actions";
    public const string QuotePicker = "mail-quote-picker";
    public const string Contacts = "mail-contacts";

    public const string UndoBar = "mail-undo-bar";
    public const string UndoBarWarning = "mail-undo-bar--warning";
    public const string UndoBarFailed = "mail-undo-bar--failed";
    public const string UndoText = "mail-undo-text";
    public const string UndoButton = "mail-undo-button";

    public const string Attachments = "mail-attachments";
    public const string AttachmentChip = "mail-attachment-chip";
    public const string AttachmentChipAll = "mail-attachment-chip--all";
    public const string AttachmentSize = "mail-attachment-size";
    public const string ImageBanner = "mail-image-banner";
    public const string SaveStatus = "mail-save-status";

    public const string Source = "mail-source";
    public const string SourceTabs = "mail-source-tabs";
    public const string SourceHeaders = "mail-source-headers";
    public const string SourceHeaderName = "mail-source-header-name";
    public const string SourceHeaderValue = "mail-source-header-value";
    public const string SourceRaw = "mail-source-raw";

    public const string Shortcuts = "mail-shortcuts";
    public const string ShortcutsTitle = "mail-shortcuts-title";
    public const string ShortcutsRow = "mail-shortcuts-row";
    public const string ShortcutsKeys = "mail-shortcuts-keys";
    public const string ShortcutsAction = "mail-shortcuts-action";
    public const string ShortcutGrid = "mail-shortcut-grid";
    public const string ModalSectionTitle = "mail-modal-section-title";
    public const string SnoozeWhen = "mail-snooze-when";

    public const string WizardTest = "wizard-test";
    public const string WizardTestFail = "wizard-test--fail";

    public const string FolderRetentionTag = "mail-folder-retention-tag";
    public const string DeletedFoldersBin = "mail-deleted-folders-bin";
    public const string DeletedFoldersBinActive = "mail-deleted-folders-bin--active";

    public const string TrashPolicyBanner = "mail-trash-policy-banner";
    public const string TrashPolicyText = "mail-trash-policy-text";
    public const string TrashPolicyActions = "mail-trash-policy-actions";

    public const string DeletedFolderCard = "mail-deleted-folder-card";
    public const string DeletedFolderMain = "mail-deleted-folder-main";
    public const string DeletedFolderTitle = "mail-deleted-folder-title";
    public const string DeletedFolderMeta = "mail-deleted-folder-meta";
    public const string DeletedFolderActions = "mail-deleted-folder-actions";

    public const string ModalBodyStack = "mail-modal-body-stack";
    public const string ModalFieldBlock = "mail-modal-field-block";
    public const string ModalFieldLabel = "mail-modal-field-label";
    public const string ModalFieldHint = "mail-modal-field-hint";
    public const string ModalRowBetween = "mail-modal-row-between";
    public const string ModalPresetBar = "mail-modal-preset-bar";
    public const string ModalSelectInput = "mail-modal-select-input";
    public const string ModalFooterActions = "mail-modal-footer-actions";
    public const string ModalErrorBanner = "mail-modal-error-banner";
    public const string ModalNumberBox = "mail-modal-number-box";
}
