using Microsoft.Extensions.Logging;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
#if WINDOWS
using H.NotifyIcon;
#endif

namespace MyLovelyMail
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            AppPaths.EnsureCreated();
            SettingsManager.LoadSettings();
            Logger.ActivateLogging = Settings.EnableLogging.Value;
            Logger.Log("App starting: paths ensured, settings loaded.");
            MainProject.Constants.ThemeConstants.ThemeManager.ApplyFromSettings();
            AccountStore.Load();
            CredentialVault.Load();
            FilterRuleStore.Load();
            TagStore.Load();
            NotificationBridge.Initialize();
#if DEBUG
            MainProject.ZTests.DebugApi.Start();
#endif
            MainProject.Services.Mail.SyncScheduler.Start();
            AccountStore.OnAccountsChanged += MainProject.Services.Mail.ImapIdleService.Refresh;
            MainProject.Services.Mail.ImapIdleService.Refresh();

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
