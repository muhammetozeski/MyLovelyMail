using MyLovelyMail.MainProject.DataModels;

namespace MyLovelyMail.MainProject.Services
{
    public static class UserProfileService
    {
        public static UserProfileData? LoggedUserProfileData { get; set; }
        public static event Action? OnProfileLoaded;
        public static event Action? OnProfileUpdated;

        public static async Task<UserProfileData?> GetProfileAsync(string userId)
        {
            // Add your generic get profile logic here
            await Task.CompletedTask;
            return LoggedUserProfileData;
        }

        public static async Task UpdateProfileAsync(UserProfileData profileData)
        {
            // Add your generic update profile logic here
            LoggedUserProfileData = profileData;
            await Task.CompletedTask;
            OnProfileUpdated?.Invoke();
        }

        public static async Task LoadProfileAsync()
        {
            // Add your load logic here
            await Task.CompletedTask;
            OnProfileLoaded?.Invoke();
        }
    }
}
