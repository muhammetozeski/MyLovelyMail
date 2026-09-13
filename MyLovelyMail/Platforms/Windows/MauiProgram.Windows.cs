using H.NotifyIcon;
using Microsoft.Win32;
using MyLovelyMail.MainProject.Constants.ThemeConstants;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail
{
    public static partial class MauiProgram
    {
        /// <summary>Leaves when another copy already owns this Windows session, then takes the run lock the deploy script reads.</summary>
        static partial void OnStartupBegin()
        {
            if (!SingleInstance.Claim())
                Environment.Exit(0);

            RunLock.Claim();
            WatchForExit();
        }

        /// <summary>Theme and animation settings from the registry, toast notifications and the MediaPlayer sound bridge.</summary>
        static partial void RegisterPlatformServices()
        {
            ThemeManager.SystemDarkProbe = SystemThemeProbe.PrefersDark;
            MotionPreference.SystemReducedMotionProbe = SystemMotionProbe.PrefersReducedMotion;
            NotificationBridge.Initialize();
            SoundBridge.Initialize();
        }

        /// <summary>The tray icon.</summary>
        static partial void ConfigurePlatformBuilder(MauiAppBuilder builder) => builder.UseNotifyIcon();

        /// <summary>
        /// Hooks every ending the process can still observe, so the run lock can name it. Task
        /// Manager's "End task", a power cut and a debugger stop go through TerminateProcess,
        /// which runs none of these - such a run simply keeps the "Unexpected" verdict the file
        /// already carries while the app is up.
        /// </summary>
        static void WatchForExit()
        {
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => RunLock.Release();
            // The runtime tears the process down straight after this handler, so ProcessExit does
            // not follow: this path has to write the file itself.
            AppDomain.CurrentDomain.UnhandledException += static (_, _) => RunLock.Release(AppExitReason.UnhandledException);
            SystemEvents.SessionEnding += static (_, e) =>
                RunLock.NoteExitReason(e.Reason == SessionEndReasons.Logoff
                    ? AppExitReason.UserLogOff
                    : AppExitReason.SystemShutdown);
        }
    }
}
