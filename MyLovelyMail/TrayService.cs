using MyLovelyMail.MainProject.Constants;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
// Aliased because the implicit usings already import System.Threading.Timer under the same name.
using Timer = System.Timers.Timer;
#if WINDOWS
using Microsoft.Win32;
// Aliased because MAUI's Window (the type of mainWindow) owns the plain name here.
using WinUiWindow = Microsoft.UI.Xaml.Window;
#endif

namespace MyLovelyMail
{
    /// <summary>
    /// Windows tray behavior: close-to-tray interception, hide/show of the main window,
    /// start-minimized handling and the "start with Windows" registry entry. On non-Windows
    /// targets every member is a no-op so callers never need their own platform guards.
    /// </summary>
    public static class TrayService
    {
        public const string AutostartValueName = AppConstants.AppName;
        public const string AutostartMinimizedArgument = "--minimized";

        static Window? mainWindow;

        /// <summary>True when the process was started with the autostart "--minimized" argument.</summary>
        public static bool LaunchedMinimized =>
            Environment.GetCommandLineArgs().Contains(AutostartMinimizedArgument, StringComparer.OrdinalIgnoreCase);

        /// <summary>Hooks the window's platform close event and applies the startup settings.</summary>
        public static void AttachWindow(Window window)
        {
            mainWindow = window;
            RestoreWindowBounds(window);
#if WINDOWS
            window.HandlerChanged += (_, _) =>
            {
                if (window.Handler?.PlatformView is not WinUiWindow platformWindow) return;

                platformWindow.AppWindow.Closing += (_, e) =>
                {
                    if (!Settings.CloseToTray.Value) return;
                    e.Cancel = true;
                    platformWindow.AppWindow.Hide();
                };

                if (Settings.StartMinimized.Value || LaunchedMinimized)
                {
                    platformWindow.AppWindow.Hide();

                    // The handler exists before WinUI activates the window, so that first Hide()
                    // is undone by the activation that follows and the app flashes up in the
                    // foreground. Hide once more on the first activation, then step aside so the
                    // user's own "show from tray" is never fought.
                    void HideOnFirstActivation(object _, Microsoft.UI.Xaml.WindowActivatedEventArgs e)
                    {
                        if (e.WindowActivationState == Microsoft.UI.Xaml.WindowActivationState.Deactivated) return;
                        platformWindow.Activated -= HideOnFirstActivation;
                        platformWindow.AppWindow.Hide();
                    }

                    platformWindow.Activated += HideOnFirstActivation;
                }
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
                if (mainWindow?.Handler?.PlatformView is not WinUiWindow platformWindow) return;
                // A window restored from poisoned bounds sits far off-screen — showing it there
                // looks exactly like "nothing happens", so always pull it back first.
                ClampToWorkArea(mainWindow);
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
                using var runKey = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (runKey == null) return;

                if (enable)
                {
                    // Only a DEPLOYED install (launcher present at the root) may own the Run entry.
                    // Dev/debug sandboxes otherwise hijack the user's autostart with a bin\ path.
                    string launcherPath = Path.Combine(AppPaths.Root, AppConstants.LauncherFileName);
                    if (!File.Exists(launcherPath))
                    {
                        Logger.Log("Autostart write skipped: not a deployed install (no root launcher).");
                        return;
                    }
                    runKey.SetValue(AutostartValueName, $"\"{launcherPath}\" {AutostartMinimizedArgument}");
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
        public static void SyncNow()
        {
            Logger.Log("Tray menu: Sync now clicked.");
            _ = SyncScheduler.SyncNowAsync();
        }

        /// <summary>Smallest believable size for a real (non-minimized) app window, in DIP.</summary>
        const int MinSaneWindowWidth = 400;
        const int MinSaneWindowHeight = 300;

        /// <summary>
        /// A minimized window reports its caption-stub bounds (X/Y around -25600, size ~159x37 DIP).
        /// Persisting or restoring those puts the window kilometers off-screen — the classic
        /// "app opens but nothing appears" state this user hit.
        /// </summary>
        static bool BoundsLookSane(double x, double y, double width, double height) =>
            x > -10000 && y > -10000 && width >= MinSaneWindowWidth && height >= MinSaneWindowHeight;

        /// <summary>Pulls the window into the visible work area (keeps at least a grabbable part on screen).</summary>
        static void ClampToWorkArea(Window window)
        {
            var screen = DeviceDisplay.MainDisplayInfo;
            double maxX = Math.Max(0, screen.Width / screen.Density - 200);
            double maxY = Math.Max(0, screen.Height / screen.Density - 200);
            window.X = Math.Clamp(window.X, 0, maxX);
            window.Y = Math.Clamp(window.Y, 0, maxY);
        }

        /// <summary>Applies saved window bounds (first run keeps defaults) and persists them debounced on move/resize.</summary>
        static void RestoreWindowBounds(Window window)
        {
            if (BoundsLookSane(Settings.WindowX.Value, Settings.WindowY.Value, Settings.WindowWidth.Value, Settings.WindowHeight.Value))
            {
                window.X = Settings.WindowX.Value;
                window.Y = Settings.WindowY.Value;
                window.Width = Settings.WindowWidth.Value;
                window.Height = Settings.WindowHeight.Value;
                ClampToWorkArea(window);
            }

            Timer? saveDebounce = null;
            window.SizeChanged += (_, _) =>
            {
                saveDebounce?.Dispose();
                saveDebounce = new Timer(800) { AutoReset = false };
                saveDebounce.Elapsed += (_, _) =>
                {
                    if (!BoundsLookSane(window.X, window.Y, window.Width, window.Height)) return;
                    Settings.WindowX.Set((int)window.X);
                    Settings.WindowY.Set((int)window.Y);
                    Settings.WindowWidth.Set((int)window.Width);
                    Settings.WindowHeight.Set((int)window.Height);
                    SettingsManager.SaveSettings();
                };
                saveDebounce.Start();
            };
        }
    }
}
