using Microsoft.Extensions.Logging;
using MyLovelyMail.MainProject.Constants.ThemeConstants;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.ZTests;
#if WINDOWS
using H.NotifyIcon;
#endif

namespace MyLovelyMail
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            if (!SingleInstance.Claim())
                Environment.Exit(0);

            AppPaths.EnsureCreated();
            // Before anything reads a folder: earlier versions wrote local folders — Drafts,
            // Outbox, Sent — into UserCache, which is the folder users are told is safe to delete.
            MessageStore.MoveLocalFoldersOutOfCache();
            RunLock.Claim();
            WatchForExit();
            SettingsManager.LoadSettings();
            Logger.ActivateLogging = Settings.EnableLogging.Value;
            Logger.Log("App starting: paths ensured, settings loaded.");
            ThemeManager.SystemDarkProbe = SystemThemeProbe.PrefersDark;
            MotionPreference.SystemReducedMotionProbe = SystemMotionProbe.PrefersReducedMotion;
            ThemeManager.ApplyFromSettings();
            AccountStore.Load();
            CredentialVault.Load();
            FilterRuleStore.Load();
            TagStore.Load();
            NotificationBridge.Initialize();
            SoundBridge.Initialize();
            // Through the UI thread: the Windows clipboard is apartment-bound, so a copy started
            // from a background thread — a side-button click arriving over the debug API, an
            // action finishing on a task — failed with an empty exception message.
            ClipboardService.Writer = static text =>
                MainThread.InvokeOnMainThreadAsync(() => Clipboard.Default.SetTextAsync(text));
#if DEBUG
            DebugApi.Start();
#endif
            SyncScheduler.Start();
            AccountStore.OnAccountsChanged += ImapIdleService.Refresh;
            ImapIdleService.Refresh();

            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });
#if WINDOWS
            builder.UseNotifyIcon();
#endif

            builder.Services.AddMauiBlazorWebView();

            return builder.Build();
        }

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
#if WINDOWS
            Microsoft.Win32.SystemEvents.SessionEnding += static (_, e) =>
                RunLock.NoteExitReason(e.Reason == Microsoft.Win32.SessionEndReasons.Logoff
                    ? AppExitReason.UserLogOff
                    : AppExitReason.SystemShutdown);
#endif
        }
    }
}
