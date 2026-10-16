using MyLovelyMail.Web.Components;

namespace MyLovelyMail
{
    public class Program
    {
        public static void Main(string[] args)
        {
            MainProject.Storage.AppPaths.EnsureCreated();
            MainProject.Stores.SettingsManager.LoadSettings();
            MainProject.Constants.ThemeConstants.ThemeManager.ApplyFromSettings();
            MainProject.Stores.AccountStore.Load();
            MainProject.Stores.CredentialVault.Load();
            MainProject.Stores.FilterRuleStore.Load();
            MainProject.Stores.TagStore.Load();

            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            var app = builder.Build();

            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            app.UseHttpsRedirection();

            app.UseAntiforgery();

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode()
                .AddAdditionalAssemblies(typeof(MainProject.UI.Routes).Assembly);

            app.Run();
        }
    }
}
