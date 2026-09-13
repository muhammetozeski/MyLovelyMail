using Android.App;

namespace MyLovelyMail.MainProject.Storage
{
    public static partial class AppPaths
    {
        /// <summary>
        /// The app's private files folder. Nothing else on the phone can read it, and Android deletes it
        /// together with the app. UserData, UserCache and AppCache are created inside it as on Windows.
        /// </summary>
        /// <param name="root">Receives the files folder's absolute path.</param>
        static partial void ResolvePlatformRoot(ref string? root) =>
            root = Application.Context.FilesDir?.AbsolutePath
                ?? throw new InvalidOperationException("Android did not hand the app its private files folder.");
    }
}
