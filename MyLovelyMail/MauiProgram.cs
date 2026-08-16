using Microsoft.Extensions.Logging;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            AppPaths.EnsureCreated();

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
