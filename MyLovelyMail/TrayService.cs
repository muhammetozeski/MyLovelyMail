using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail
{
    /// <summary>
    /// Windows tray behavior: close-to-tray interception, hide/show of the main window,
    /// start-minimized handling and the "start with Windows" registry entry. On non-Windows
    /// targets every member is a no-op so callers never need their own platform guards.
    /// </summary>
    public static class TrayService
    {
        public const string AutostartValueName = "MyLovelyMail";
        public const string AutostartMinimizedArgument = "--minimized";

        static Window? mainWindow;

        /// <summary>True when the process was started with the autostart "--minimized" argument.</summary>
        public static bool LaunchedMinimized =>
            Environment.GetCommandLineArgs().Contains(AutostartMinimizedArgument, StringComparer.OrdinalIgnoreCase);

        /// <summary>Hooks the window's platform close event and applies the startup settings.</summary>
        public static void AttachWindow(Window window)
        {
            mainWindow = window;
#if WINDOWS
            window.HandlerChanged += (_, _) =>
            {
                if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window platformWindow) return;

                platformWindow.AppWindow.Closing += (_, e) =>
                {
                    if (!Settings.CloseToTray.Value) return;
                    e.Cancel = true;
                    platformWindow.AppWindow.Hide();
                };

                if (Settings.StartMinimized.Value || LaunchedMinimized)
                    platformWindow.AppWindow.Hide();
            };

            Settings.StartWithWindows.OnChanged += ApplyAutostart;
            ApplyAutostart(Settings.StartWithWindows.Value);
#endif
        }

        /// <summary>Brings the main window back from the tray and focuses it.</summary>
        public static void ShowMainWindow()
        {
#if WINDOWS
            mainWindow?.Dispatcher.Dispatch(() =>
            {
                if (mainWindow?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window platformWindow) return;
                platformWindow.AppWindow.Show();
                platformWindow.Activate();
            });
#endif
        }

        public static void ExitApplication()
        {
#if WINDOWS
            // Quit() respects nothing about our close-to-tray interception — the Closing handler
            // cancels it. Drop the interception by clearing the setting flag in memory only.
            Settings.CloseToTray.Value = false;
#endif
            Application.Current?.Quit();
        }

        /// <summary>Writes or removes the HKCU Run entry. Points at the launcher when deployed, else this exe.</summary>
        static void ApplyAutostart(bool enable)
        {
#if WINDOWS
            try
            {
                using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (runKey == null) return;

                if (enable)
                {
                    string launcherPath = Path.Combine(MainProject.Storage.AppPaths.Root, "MyLovelyMail.exe");
                    string exePath = File.Exists(launcherPath) ? launcherPath : Environment.ProcessPath ?? launcherPath;
                    runKey.SetValue(AutostartValueName, $"\"{exePath}\" {AutostartMinimizedArgument}");
                }
                else
                {
                    runKey.DeleteValue(AutostartValueName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Autostart registry update failed: {ex.Message}", Logger.LogLevel.Error);
            }
#endif
        }

        /// <summary>Fire-and-forget manual sync used by the tray menu.</summary>
        public static void SyncNow() => _ = SyncScheduler.SyncNowAsync();
    }
}
