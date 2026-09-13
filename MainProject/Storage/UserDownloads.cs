namespace MyLovelyMail.MainProject.Storage
{
    /// <summary>
    /// Where a file the user asked to save — an attachment, an exported message, a folder as mbox —
    /// ends up. Every save writes into <see cref="WriteFolder"/> and then calls <see cref="Publish"/>.
    /// On Windows that folder is the user's Downloads folder itself and publishing changes nothing.
    /// Android does not let an app write into the shared Download folder by path, so there the save
    /// goes into a staging folder and publishing hands the finished file to Android.
    /// </summary>
    public static partial class UserDownloads
    {
        /// <summary>The folder a save writes its file (or its folder of files) into.</summary>
        public static string WriteFolder
        {
            get
            {
                string? platformFolder = null;
                ResolvePlatformWriteFolder(ref platformFolder);
                if (platformFolder != null) return platformFolder;

                string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                return Directory.Exists(downloads) ? downloads : AppPaths.UserData;
            }
        }

        /// <summary>Makes a finished save visible where the user looks for downloads.</summary>
        /// <param name="writtenPath">A file or a folder of files written under <see cref="WriteFolder"/>.</param>
        /// <returns>Where the user finds it now, for the status line.</returns>
        public static string Publish(string writtenPath)
        {
            string location = writtenPath;
            PublishOnPlatform(writtenPath, ref location);
            return location;
        }

        /// <summary>Implemented by a platform that saves into a staging folder first; the others write into Downloads directly.</summary>
        /// <param name="folder">Set to the staging folder.</param>
        static partial void ResolvePlatformWriteFolder(ref string? folder);

        /// <summary>Implemented by a platform that has to move a staged save to where downloads are shown.</summary>
        /// <param name="writtenPath">The staged file or folder.</param>
        /// <param name="location">Set to where the user finds the result.</param>
        static partial void PublishOnPlatform(string writtenPath, ref string location);
    }
}
