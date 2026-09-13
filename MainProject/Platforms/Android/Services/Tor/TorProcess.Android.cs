using Android.App;

namespace MyLovelyMail.MainProject.Services.Tor
{
    public static partial class TorProcess
    {
        /// <summary>
        /// libtor.so, which the APK carries in lib/arm64-v8a and Android extracts into the app's native
        /// library directory. That directory is the one place Android lets an app execute a file from;
        /// a binary copied into the app's own writable folders would be refused.
        /// </summary>
        static partial void ResolveBundledExecutable(ref string? path) =>
            path = Path.Combine(Application.Context.ApplicationInfo?.NativeLibraryDir
                ?? throw new InvalidOperationException("Android did not report the app's native library directory."), "libtor.so");
    }
}
