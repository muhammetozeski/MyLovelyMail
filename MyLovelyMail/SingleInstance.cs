#if WINDOWS
using System.Runtime.InteropServices;
#endif

namespace MyLovelyMail
{
    /// <summary>
    /// One running copy per user session. The launcher happily starts a new process on every
    /// shortcut click, so without this a user who has the app hidden in the tray collects
    /// duplicate instances (each with its own sync loops) instead of getting their window back.
    /// </summary>
    public static class SingleInstance
    {
#if WINDOWS
        const int SW_RESTORE = 9;

        // Held for the whole process lifetime; never disposed on purpose.
        static Mutex? claimedMutex;

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string? className, string windowTitle);

        /// <summary>
        /// True = this process owns the app from now on. False = another instance is already
        /// running: its window was brought to the front (unless this copy was started
        /// "--minimized", where staying invisible is the whole point) and the caller must exit.
        /// </summary>
        public static bool Claim()
        {
#if DEBUG
            // The dev sandbox must coexist with the user's deployed install (separate UserData,
            // separate window) — sharing one mutex would treat them as duplicates of each other.
            const string MutexName = @"Local\MyLovelyMail-single-instance-debug";
#else
            const string MutexName = @"Local\MyLovelyMail-single-instance";
#endif
            claimedMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (createdNew) return true;

            // Logger is not up this early, so the duplicate leaves a plain-text trace instead —
            // it identifies WHO keeps launching argument-less copies at boot (shortcut vs restore).
            try
            {
                string tracePath = Path.Combine(MainProject.Storage.AppPaths.AppCache, "second-instance-trace.log");
                Directory.CreateDirectory(Path.GetDirectoryName(tracePath)!);
                File.AppendAllText(tracePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} duplicate start, args=[{string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}]{Environment.NewLine}");
            }
            catch { /* tracing must never block startup */ }

            if (!TrayService.LaunchedMinimized)
            {
                IntPtr window = FindWindowW(null, "My Lovely Mail");
                if (window != IntPtr.Zero)
                {
                    ShowWindow(window, SW_RESTORE);
                    SetForegroundWindow(window);
                }
            }
            return false;
        }
#else
        public static bool Claim() => true;
#endif
    }
}
