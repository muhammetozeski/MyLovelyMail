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
            SettingsManager.LoadSettings();
            Logger.ActivateLogging = Settings.EnableLogging.Value;
            Logger.Log("App starting: paths ensured, settings loaded.");
            ThemeManager.SystemDarkProbe = SystemThemeProbe.PrefersDark;
            ThemeManager.ApplyFromSettings();
            AccountStore.Load();
            CredentialVault.Load();
            FilterRuleStore.Load();
            TagStore.Load();
            NotificationBridge.Initialize();
            SoundBridge.Initialize();
            ClipboardService.Writer = static text => Clipboard.Default.SetTextAsync(text);
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
    }
}
