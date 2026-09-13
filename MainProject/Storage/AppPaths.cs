using MyLovelyMail.MainProject.Constants;

namespace MyLovelyMail.MainProject.Storage
{
    /// <summary>
    /// Resolves the on-disk folder layout and creates it at startup. The deployed layout is:
    /// <c>MyLovelyMail\</c> (root, holds the launcher) with <c>AppData</c> (the application files),
    /// <c>UserData</c> (settings, accounts, rules — the user's own data), <c>UserCache</c>
    /// (re-downloadable user content such as synced mail; deleting it never locks the user out)
    /// and <c>AppCache</c> (app-only re-creatable content, unrelated to the user). Every disk
    /// access in the app must build its path from this class.
    /// </summary>
    public static partial class AppPaths
    {
        public const string RootFolderName = AppConstants.AppName;
        public const string AppFolderName = "AppData";
        public const string UserDataFolderName = "UserData";
        public const string UserCacheFolderName = "UserCache";
        public const string AppCacheFolderName = "AppCache";

        /// <summary>
        /// The MyLovelyMail root folder. When the executable runs from the deployed
        /// <c>&lt;Root&gt;\AppData</c> folder the root is that folder's parent; any other run
        /// (IDE, loose bin folder) gets a self-contained sandbox root next to the executable,
        /// so development never touches a real installation.
        /// </summary>
        public static string Root { get; } = ResolveRoot();

        /// <summary> Application files folder (the deployed executable lives here). </summary>
        public static string AppFolder => Path.Combine(Root, AppFolderName);

        /// <summary> The user's own data: settings, accounts, rules, credential vault. </summary>
        public static string UserData => Path.Combine(Root, UserDataFolderName);

        /// <summary> Re-downloadable user content (synced mail, remote avatars). Safe to delete: the app re-fetches everything. </summary>
        public static string UserCache => Path.Combine(Root, UserCacheFolderName);

        /// <summary> App-only re-creatable content with no relation to the user (update payloads, prefetched assets). Safe to delete. </summary>
        public static string AppCache => Path.Combine(Root, AppCacheFolderName);

        static string ResolveRoot()
        {
            string? platformRoot = null;
            ResolvePlatformRoot(ref platformRoot);
            if (platformRoot != null) return platformRoot;

            string baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(Path.GetFileName(baseDirectory), AppFolderName, StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(baseDirectory)!
                : Path.Combine(baseDirectory, RootFolderName);
        }

        /// <summary>
        /// Filled in by a platform whose executable folder is not the app's to write in; on Android the
        /// app lives inside its package and gets a private files folder from the system instead. Left
        /// empty on Windows and in the web preview, which keep the folder-next-to-the-executable layout.
        /// </summary>
        /// <param name="root">Set to the root folder to use; left null keeps the layout above.</param>
        static partial void ResolvePlatformRoot(ref string? root);

        /// <summary>
        /// Creates the whole folder layout (root + user/cache folders). Called once at startup;
        /// calling it again is harmless, so any code unsure about first-run state may call it too.
        /// </summary>
        public static void EnsureCreated()
        {
            Directory.CreateDirectory(UserData);
            Directory.CreateDirectory(UserCache);
            Directory.CreateDirectory(AppCache);
        }
    }
}
