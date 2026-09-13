using Microsoft.Extensions.Logging;
using MyLovelyMail.MainProject.Constants.ThemeConstants;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;
using MyLovelyMail.MainProject.ZTests;

namespace MyLovelyMail
{
    /// <summary>
    /// The startup order every platform shares. The steps that differ per platform are the partial
    /// methods at the bottom, implemented in Platforms\Windows and Platforms\Android; a platform with
    /// nothing to do at a step leaves it unimplemented and the call compiles away.
    /// </summary>
    public static partial class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            OnStartupBegin();

            AppPaths.EnsureCreated();
            // Before anything reads a folder: earlier versions wrote local folders — Drafts,
            // Outbox, Sent — into UserCache, which is the folder users are told is safe to delete.
            MessageStore.MoveLocalFoldersOutOfCache();
            SettingsManager.LoadSettings();
            Logger.ActivateLogging = Settings.EnableLogging.Value;
            Logger.Log("App starting: paths ensured, settings loaded.");
            RegisterPlatformServices();
            ThemeManager.ApplyFromSettings();
            AccountStore.Load();
            CredentialVault.Load();
            FilterRuleStore.Load();
            TagStore.Load();
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
            ConfigurePlatformBuilder(builder);

            builder.Services.AddMauiBlazorWebView();

            return builder.Build();
        }

        /// <summary>Runs before anything touches the disk, so a copy of the app that must not run can still leave without side effects.</summary>
        static partial void OnStartupBegin();

        /// <summary>
        /// Hands MainProject the pieces only the platform can provide: the theme and motion probes, the
        /// new-mail notification presenter, the sound player and the vault's device protection. Settings
        /// are loaded by now; the theme and the stores are not, because both read what is set here.
        /// </summary>
        static partial void RegisterPlatformServices();

        /// <summary>Adds the platform's own MAUI handlers.</summary>
        /// <param name="builder">The builder that already has the app and its fonts.</param>
        static partial void ConfigurePlatformBuilder(MauiAppBuilder builder);
    }
}
