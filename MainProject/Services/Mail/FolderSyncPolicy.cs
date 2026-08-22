using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Which way each kind of folder syncs. The rules existed before this class did, but spread
    /// across the code as the shape of whatever happened to be written: drafts stayed local
    /// because the draft code wrote them locally, not because anyone had decided drafts are local.
    /// A rule nobody wrote down is a rule the next feature does not inherit.
    /// <para>
    /// Every direction is a setting, global and per account, so the defaults below are defaults
    /// rather than the law.
    /// </para>
    /// </summary>
    public static class FolderSyncPolicy
    {
        public static FolderSyncDirection For(string accountId, FolderRole role)
        {
            var settings = AccountStore.GetSettings(accountId);
            return role switch
            {
                FolderRole.Inbox => settings.InboxSync.Value,
                FolderRole.Drafts => settings.DraftsSync.Value,
                FolderRole.Sent => settings.SentSync.Value,
                FolderRole.Trash => settings.TrashSync.Value,
                _ => settings.OtherFolderSync.Value
            };
        }

        /// <summary>
        /// The direction for a folder by path. A local folder is LocalOnly whatever the settings
        /// say — there is no server copy for the setting to have an opinion about.
        /// </summary>
        public static FolderSyncDirection For(string accountId, string folderFullName)
        {
            if (folderFullName.StartsWith(MessageStore.LocalFolderPrefix, StringComparison.Ordinal))
                return FolderSyncDirection.LocalOnly;

            var folder = MessageStore.GetFolders(accountId).FirstOrDefault(f => f.FullName == folderFullName);
            if (folder is { IsLocal: true }) return FolderSyncDirection.LocalOnly;
            return For(accountId, folder?.Role ?? FolderRole.None);
        }

        /// <summary>True when the app may fetch this folder's messages from the server.</summary>
        public static bool PullsFromServer(string accountId, string folderFullName) =>
            For(accountId, folderFullName) is FolderSyncDirection.ServerToLocal or FolderSyncDirection.TwoWay;

        /// <summary>True when a local change here (read, star, delete, move) may be pushed to the server.</summary>
        public static bool PushesToServer(string accountId, string folderFullName) =>
            For(accountId, folderFullName) is FolderSyncDirection.LocalToServer or FolderSyncDirection.TwoWay;
    }
}
