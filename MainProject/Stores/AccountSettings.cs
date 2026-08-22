using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>
    /// Per-account settings. Every field is an <see cref="InheritedSetting{T}"/> whose parent is
    /// the matching global field in <see cref="Settings"/>: while not overridden the account reads
    /// the global value LIVE, and only overridden entries are written to the account's settings
    /// file — so a global change reaches every account that never diverged from it.
    /// </summary>
    public class AccountSettings
    {
        public const string AccountsFolderName = "Accounts";
        public const string SettingsFileName = "settings.txt";

        /// <inheritdoc cref="Settings.SyncIntervalMinutes"/>
        public readonly InheritedSetting<int> SyncIntervalMinutes = new(Settings.SyncIntervalMinutes);

        /// <inheritdoc cref="Settings.UseImapIdle"/>
        public readonly InheritedSetting<bool> UseImapIdle = new(Settings.UseImapIdle);

        /// <inheritdoc cref="Settings.BackgroundFolderRefreshCount"/>
        public readonly InheritedSetting<int> BackgroundFolderRefreshCount = new(Settings.BackgroundFolderRefreshCount);

        /// <inheritdoc cref="Settings.MirrorRuleMovesLocally"/>
        public readonly InheritedSetting<bool> MirrorRuleMovesLocally = new(Settings.MirrorRuleMovesLocally);

        /// <inheritdoc cref="Settings.DownloadAttachmentsAutomatically"/>
        public readonly InheritedSetting<bool> DownloadAttachmentsAutomatically = new(Settings.DownloadAttachmentsAutomatically);

        /// <inheritdoc cref="Settings.OfflineKeepDays"/>
        public readonly InheritedSetting<int> OfflineKeepDays = new(Settings.OfflineKeepDays);

        /// <inheritdoc cref="Settings.OfflineMaxCacheMb"/>
        public readonly InheritedSetting<int> OfflineMaxCacheMb = new(Settings.OfflineMaxCacheMb);

        /// <inheritdoc cref="Settings.MaxAttachmentTotalMb"/>
        public readonly InheritedSetting<int> MaxAttachmentTotalMb = new(Settings.MaxAttachmentTotalMb);

        /// <inheritdoc cref="Settings.MarkAsReadDelaySeconds"/>
        public readonly InheritedSetting<int> MarkAsReadDelaySeconds = new(Settings.MarkAsReadDelaySeconds);

        /// <inheritdoc cref="Settings.ConversationView"/>
        public readonly InheritedSetting<bool> ConversationView = new(Settings.ConversationView);

        /// <inheritdoc cref="Settings.ExternalImages"/>
        public readonly InheritedSetting<ExternalImagesPolicy> ExternalImages = new(Settings.ExternalImages);

        /// <inheritdoc cref="Settings.DeleteAction"/>
        public readonly InheritedSetting<DeleteBehavior> DeleteAction = new(Settings.DeleteAction);

        /// <inheritdoc cref="Settings.Signature"/>
        public readonly InheritedSetting<string> Signature = new(Settings.Signature);

        /// <inheritdoc cref="Settings.UndoSendSeconds"/>
        public readonly InheritedSetting<int> UndoSendSeconds = new(Settings.UndoSendSeconds);

        /// <inheritdoc cref="Settings.ReplyQuoteStyle"/>
        public readonly InheritedSetting<QuoteStyle> ReplyQuoteStyle = new(Settings.ReplyQuoteStyle);

        /// <inheritdoc cref="Settings.QuoteTrimLines"/>
        public readonly InheritedSetting<int> QuoteTrimLines = new(Settings.QuoteTrimLines);

        /// <inheritdoc cref="Settings.Pop3FetchLimit"/>
        public readonly InheritedSetting<int> Pop3FetchLimit = new(Settings.Pop3FetchLimit);

        /// <inheritdoc cref="Settings.NotifyOnNewMail"/>
        public readonly InheritedSetting<bool> NotifyOnNewMail = new(Settings.NotifyOnNewMail);

        /// <inheritdoc cref="Settings.NotificationSound"/>
        public readonly InheritedSetting<string> NotificationSound = new(Settings.NotificationSound);

        public readonly string AccountId;

        readonly Dictionary<string, ISettingSetup> iSettingSetups = [];
        readonly Dictionary<string, ISetting> iSettings = [];

        public string AccountFolder => Path.Combine(AppPaths.UserData, AccountsFolderName, AccountId);
        public string SettingsPath => Path.Combine(AccountFolder, SettingsFileName);

        public AccountSettings(string accountId)
        {
            AccountId = accountId;
            SettingRegistration.RegisterFields(GetType(), this, iSettingSetups, iSettings);
        }

        public ISetting[] GetAllSettings() => [.. iSettings.Values];

        /// <summary>Loads this account's overrides from disk (keys present in the file become overridden).</summary>
        public void Load() => SettingsFile.Load(SettingsPath, iSettingSetups);

        /// <summary>Writes ONLY the overridden entries, so non-overridden settings keep following the global value.</summary>
        public void Save() =>
            SettingsFile.Save(SettingsPath,
                iSettingSetups.Values.Where(s => s is ISetting { IsDefault: false }),
                $"MyLovelyMail account settings — {AccountId} (only overridden keys are stored)");
    }
}
