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

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents()
                .AddInteractiveWebAssemblyComponents();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseWebAssemblyDebugging();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            app.UseHttpsRedirection();

            app.UseAntiforgery();

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode()
                .AddInteractiveWebAssemblyRenderMode()
                .AddAdditionalAssemblies(
                    typeof(MyLovelyMail.MainProject.UI.Routes).Assembly,
                    typeof(MyLovelyMail.Web.Client._Imports).Assembly);

            app.Run();
        }
    }
}
