using MyLovelyMail.MainProject.Constants.ThemeConstants;
using MyLovelyMail.MainProject.Services;

namespace MyLovelyMail
{
    public static partial class MauiProgram
    {
        /// <summary>The phone's dark theme and animation switch, new-mail notifications and the notification sound player.</summary>
        static partial void RegisterPlatformServices()
        {
            ThemeManager.SystemDarkProbe = AndroidDisplayPreferences.PrefersDark;
            MotionPreference.SystemReducedMotionProbe = AndroidDisplayPreferences.PrefersReducedMotion;
            MailNotifications.CreateChannel();
            NotificationService.Presenter = MailNotifications.Show;
            SoundService.Player = static soundName => _ = NotificationSoundPlayer.PlayAsync(soundName);
        }
    }
}
