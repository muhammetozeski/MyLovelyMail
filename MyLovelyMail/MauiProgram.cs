using Microsoft.Extensions.Logging;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            AppPaths.EnsureCreated();
            SettingsManager.LoadSettings();
            MainProject.Constants.ThemeConstants.ThemeManager.ApplyFromSettings();
            AccountStore.Load();

            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();


            return builder.Build();
        }
    }
}
