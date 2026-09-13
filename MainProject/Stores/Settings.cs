using MyLovelyMail.MainProject.DataModels.Mail;

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

    /// <summary>How much of the original a reply carries forward.</summary>
    public enum QuoteStyle
    {
        Full,
        Trimmed,
        None
    }

    /// <summary>How the credential vault encrypts stored passwords.</summary>
    public enum VaultMode
    {
        /// <summary>Bound to this device through <see cref="DeviceProtection"/>: DPAPI on Windows, the Android Keystore on a phone.</summary>
        DeviceBound,

        /// <summary>AES-GCM under a key derived from a password the user types at every start.</summary>
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

        /// <summary>Pick the light/dark theme automatically from the operating system's app theme instead of the fixed Theme value.</summary>
        public static readonly Setting<bool> FollowSystemTheme = new(false);

        /// <summary>Global UI scale multiplier in percent (100 = design size).</summary>
        public static readonly Setting<int> UiScalePercent = new(100);

        /// <summary>Message list density: "Cozy", "Comfortable" or "Compact".</summary>
        public static readonly Setting<string> MessageListDensity = new("Comfortable");

        /// <summary>Stop looping animations (aurora drift, sync heart, pulses) and shorten transitions.</summary>
        public static readonly Setting<bool> ReduceMotion = new(false);

        /// <summary>Take the calm-motion answer from the operating system's animation setting instead of the fixed value above.</summary>
        public static readonly Setting<bool> FollowSystemMotion = new(true);

        /// <summary>Show the unread-count badge on folders and the tray icon.</summary>
        public static readonly Setting<bool> ShowUnreadBadge = new(true);

        // ---- Sync ----

        /// <summary>Minutes between automatic mail syncs when IMAP IDLE is not active.</summary>
        public static readonly Setting<int> SyncIntervalMinutes = new(5);

        /// <summary>Keep an IMAP IDLE connection open so new mail arrives instantly.</summary>
        public static readonly Setting<bool> UseImapIdle = new(true);

        /// <summary>How many of the stalest non-Inbox folders each sync pass also refreshes (0 = Inbox only).</summary>
        public static readonly Setting<int> BackgroundFolderRefreshCount = new(2);

        /// <summary>
        /// When a rule moves a message on the server, move the cached copy the same way. Off keeps
        /// the local copy where it is, so this machine's filing can differ from the server's.
        /// </summary>
        public static readonly Setting<bool> MirrorRuleMovesLocally = new(true);

        // ---- Which way each kind of folder syncs (see FolderSyncPolicy) ----

        /// <summary>Inbox: new mail comes down, deletions and flags go up.</summary>
        public static readonly Setting<FolderSyncDirection> InboxSync = new(FolderSyncDirection.TwoWay);

        /// <summary>Drafts: written here, kept here. The server's drafts folder is listed but not fetched.</summary>
        public static readonly Setting<FolderSyncDirection> DraftsSync = new(FolderSyncDirection.LocalOnly);

        /// <summary>Sent: both ways — mail sent from another device belongs in this list too.</summary>
        public static readonly Setting<FolderSyncDirection> SentSync = new(FolderSyncDirection.TwoWay);

        /// <summary>Trash: both ways.</summary>
        public static readonly Setting<FolderSyncDirection> TrashSync = new(FolderSyncDirection.TwoWay);

        /// <summary>Every other folder — junk, archive, and the ones the user made.</summary>
        public static readonly Setting<FolderSyncDirection> OtherFolderSync = new(FolderSyncDirection.TwoWay);

        /// <summary>Download attachments together with the message body instead of on first open.</summary>
        public static readonly Setting<bool> DownloadAttachmentsAutomatically = new(false);

        /// <summary>How many days of mail to keep offline in UserCache (0 = everything).</summary>
        public static readonly Setting<int> OfflineKeepDays = new(0);

        /// <summary>Megabytes of cached message bodies an account may hold; oldest go first above it (0 = no limit).</summary>
        public static readonly Setting<int> OfflineMaxCacheMb = new(0);

        /// <summary>Encoded megabytes of attachments a message may carry before the pre-send list warns (0 = never warn).</summary>
        public static readonly Setting<int> MaxAttachmentTotalMb = new(20);

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

        /// <summary>Text size inside the reader as a percentage of the design size (clamped 70-200).</summary>
        public static readonly Setting<int> ReaderTextScalePercent = new(100);

        /// <summary>Collapses the quoted history under a reply behind a "Show quoted text" fold.</summary>
        public static readonly Setting<bool> FoldQuotedText = new(true);

        // ---- Composing ----

        /// <summary>Plain-text signature appended under new, reply and forward drafts, empty = none (the compose body is plain text, so it is not HTML).</summary>
        public static readonly Setting<string> Signature = new("");

        /// <summary>How much of the original a reply or forward quotes back.</summary>
        public static readonly Setting<QuoteStyle> ReplyQuoteStyle = new(QuoteStyle.Full);

        /// <summary>Lines of the original kept in Trimmed mode before the rest is summarised away.</summary>
        public static readonly Setting<int> QuoteTrimLines = new(30);

        /// <summary>Seconds the outbox holds a sent message for "undo send" (0 = send immediately).</summary>
        public static readonly Setting<int> UndoSendSeconds = new(5);

        /// <summary>How many newest messages a POP3 sync may fetch; 0 fetches the entire mailbox.</summary>
        public static readonly Setting<int> Pop3FetchLimit = new(300);

        // ---- Notifications ----

        /// <summary>Show a system notification when new mail arrives.</summary>
        public static readonly Setting<bool> NotifyOnNewMail = new(true);

        /// <summary>Notification sound name from Resources/Sounds ("none" silences, filters can override per rule).</summary>
        public static readonly Setting<string> NotificationSound = new("default");

        /// <summary>Quiet-hours window (local hour 0-23) during which notifications are held back. Start==End disables it.</summary>
        public static readonly Setting<int> QuietHoursStart = new(0);
        public static readonly Setting<int> QuietHoursEnd = new(0);

        // ---- Window and startup (Windows only; see PlatformFeatures.HasDesktopWindow) ----

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
        public static readonly Setting<VaultMode> CredentialVaultMode = new(VaultMode.DeviceBound);

        // ---- Tor ----
        // These describe how to REACH Tor. Whether an account may only speak through it is the
        // account's own TorOnly flag, not a setting: it belongs to the mailbox, not to this machine.

        /// <summary>Address of the Tor SOCKS proxy. Loopback by default — a remote proxy would carry the traffic in the clear to it.</summary>
        public static readonly Setting<string> TorSocksHost = new("127.0.0.1");

        /// <summary>Port of the Tor SOCKS proxy. 9050 is the tor daemon's; a running Tor Browser uses 9150, which is tried as well.</summary>
        public static readonly Setting<int> TorSocksPort = new(9050);

        /// <summary>Start a tor of the app's own when no SOCKS proxy answers. Off means a Tor-only account simply stays offline until one does.</summary>
        public static readonly Setting<bool> TorAutoStart = new(true);

        /// <summary>Full path to a tor executable. Empty searches PATH, the app folder and the usual Tor Browser locations.</summary>
        public static readonly Setting<string> TorExecutablePath = new("");

        /// <summary>Seconds to wait for a freshly started tor to finish bootstrapping. A first run on a slow link genuinely takes minutes.</summary>
        public static readonly Setting<int> TorStartupTimeoutSeconds = new(180);

        /// <summary>
        /// Bridge lines for a network that blocks Tor itself, one per line, exactly as
        /// bridges.torproject.org hands them out — "obfs4 10.0.0.1:443 FINGERPRINT cert=… iat-mode=0",
        /// or a bare "10.0.0.1:9001 FINGERPRINT" for a plain bridge. Empty uses the public network.
        /// <para>
        /// Stored with the unit-separator control character between lines, like SavedSearches: the
        /// settings file is one key per line, so a real newline would end the value.
        /// </para>
        /// </summary>
        public static readonly Setting<string> TorBridgeLines = new("");

        // ---- Diagnostics ----

        /// <summary>Master logging on/off. On by default: entries buffer in RAM for the log viewer and persist scrambled to AppCache/Logs.</summary>
        public static readonly Setting<bool> EnableLogging = new(true);
    }
}
