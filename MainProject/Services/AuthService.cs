using MyLovelyMail.MainProject.DataModels;

namespace MyLovelyMail.MainProject.Services
{
    public static class AuthService
    {
        public static event Action? OnAuthStateChanged;
        public static bool IsLoggedIn { get; set; }

        public static async Task InitAsync()
        {
            await Task.CompletedTask;
        }
    }
}
