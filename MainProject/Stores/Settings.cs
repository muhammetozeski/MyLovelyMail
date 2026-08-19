namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>How deleting a message behaves.</summary>
    public enum DeleteBehavior
    {
        MoveToTrash,
        ArchiveInstead,
        DeletePermanently
    }

    /// <summary>When remote images inside HTML mail are loaded.</summary>
    public enum ExternalImagesPolicy
    {
        Block,
        AllowFromContacts,
        AllowAlways
    }

    /// <summary>How the credential vault encrypts stored passwords.</summary>
    public enum VaultMode
    {
        Dpapi,
        MasterPassword
    }

    /// <summary>
    /// All globally accessible settings. Each public static readonly Setting&lt;T&gt; field is
    /// auto-registered by <see cref="SettingsManager"/> (its key is the field name). Per-account
    /// overrides of the mail-related subset live in <see cref="AccountSettings"/> and inherit
    /// from these fields live until overridden.
    /// </summary>
    public static class Settings
    {
        // ---- Appearance ----

        /// <summary>Name of the active theme in AppThemes ("LovelyBloom" pastel default, "PlayfulStarlight" dark).</summary>
        public static readonly Setting<string> Theme = new("LovelyBloom");

        /// <summary>Pick the light/dark theme automatically from the Windows app theme instead of the fixed Theme value.</summary>
        public static readonly Setting<bool> FollowSystemTheme = new(false);

        /// <summary>Global UI scale multiplier in percent (100 = design size).</summary>
        public static readonly Setting<int> UiScalePercent = new(100);

        /// <summary>Message list density: "Cozy", "Comfortable" or "Compact".</summary>
        public static readonly Setting<string> MessageListDensity = new("Comfortable");

        /// <summary>Stop looping animations (aurora drift, sync heart, pulses) and shorten transitions.</summary>
        public static readonly Setting<bool> ReduceMotion = new(false);

        /// <summary>Take the calm-motion answer from the Windows animation setting instead of the fixed value above.</summary>
        public static readonly Setting<bool> FollowSystemMotion = new(true);

        /// <summary>Show the unread-count badge on folders and the tray icon.</summary>
        public static readonly Setting<bool> ShowUnreadBadge = new(true);

        // ---- Sync ----

        /// <summary>Minutes between automatic mail syncs when IMAP IDLE is not active.</summary>
        public static readonly Setting<int> SyncIntervalMinutes = new(5);

        /// <summary>Keep an IMAP IDLE connection open so new mail arrives instantly.</summary>
        public static readonly Setting<bool> UseImapIdle = new(true);

        /// <summary>Download attachments together with the message body instead of on first open.</summary>
        public static readonly Setting<bool> DownloadAttachmentsAutomatically = new(false);

        /// <summary>How many days of mail to keep offline in UserCache (0 = everything).</summary>
        public static readonly Setting<int> OfflineKeepDays = new(0);

        /// <summary>Megabytes of cached message bodies an account may hold; oldest go first above it (0 = no limit).</summary>
        public static readonly Setting<int> OfflineMaxCacheMb = new(0);

        // ---- Reading ----

        /// <summary>Seconds a message must stay open before it is marked read (0 = immediately).</summary>
        public static readonly Setting<int> MarkAsReadDelaySeconds = new(0);

        /// <summary>Group messages of the same thread into conversations in the list.</summary>
        public static readonly Setting<bool> ConversationView = new(true);

        /// <summary>Privacy policy for remote images inside HTML mail.</summary>
        public static readonly Setting<ExternalImagesPolicy> ExternalImages = new(ExternalImagesPolicy.Block);

        /// <summary>What happens when the user deletes a message.</summary>
        public static readonly Setting<DeleteBehavior> DeleteAction = new(DeleteBehavior.MoveToTrash);

        // ---- Reading ----

        /// <summary>Collapses the quoted history under a reply behind a "Show quoted text" fold.</summary>
        public static readonly Setting<bool> FoldQuotedText = new(true);

        // ---- Composing ----

        /// <summary>Plain-text signature appended under new, reply and forward drafts, empty = none (the compose body is plain text, so it is not HTML).</summary>
        public static readonly Setting<string> Signature = new("");

        /// <summary>Seconds the outbox holds a sent message for "undo send" (0 = send immediately).</summary>
        public static readonly Setting<int> UndoSendSeconds = new(5);

        /// <summary>How many newest messages a POP3 sync may fetch; 0 fetches the entire mailbox.</summary>
        public static readonly Setting<int> Pop3FetchLimit = new(300);

        // ---- Notifications ----

        /// <summary>Show a Windows notification when new mail arrives.</summary>
        public static readonly Setting<bool> NotifyOnNewMail = new(true);

        /// <summary>Notification sound name from Resources/Sounds ("none" silences, filters can override per rule).</summary>
        public static readonly Setting<string> NotificationSound = new("default");

        /// <summary>Quiet-hours window (local hour 0-23) during which notifications are held back. Start==End disables it.</summary>
        public static readonly Setting<int> QuietHoursStart = new(0);
        public static readonly Setting<int> QuietHoursEnd = new(0);

        // ---- Window and startup ----

        /// <summary>Closing the window hides to the system tray instead of exiting.</summary>
        public static readonly Setting<bool> CloseToTray = new(true);

        /// <summary>Start hidden in the tray instead of showing the window.</summary>
        public static readonly Setting<bool> StartMinimized = new(false);

        /// <summary>Launch MyLovelyMail automatically at Windows sign-in.</summary>
        public static readonly Setting<bool> StartWithWindows = new(false);

        // ---- Search ----

        /// <summary>Saved search queries, separated by the unit-separator control char (it cannot occur in typed text).</summary>
        public static readonly Setting<string> SavedSearches = new("");

        // ---- Window bounds (0 width/height = first run, keep platform defaults) ----

        public static readonly Setting<int> WindowX = new(0);
        public static readonly Setting<int> WindowY = new(0);
        public static readonly Setting<int> WindowWidth = new(0);
        public static readonly Setting<int> WindowHeight = new(0);

        // ---- Security ----

        /// <summary>How the credential vault encrypts account passwords on disk.</summary>
        public static readonly Setting<VaultMode> CredentialVaultMode = new(VaultMode.Dpapi);

        // ---- Diagnostics ----

        /// <summary>Master logging on/off. On by default: entries buffer in RAM for the log viewer and persist scrambled to AppCache/Logs.</summary>
        public static readonly Setting<bool> EnableLogging = new(true);
    }
}
