using MyLovelyMail.MainProject.Constants.ThemeConstants;
using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Services.Mail;

namespace MyLovelyMail
{
    public static partial class MauiProgram
    {
        /// <summary>
        /// The phone's dark theme and animation switch, new-mail notifications, the notification sound
        /// player, and the foreground service that keeps the sync loop running.
        /// </summary>
        static partial void RegisterPlatformServices()
        {
            ThemeManager.SystemDarkProbe = AndroidDisplayPreferences.PrefersDark;
            MotionPreference.SystemReducedMotionProbe = AndroidDisplayPreferences.PrefersReducedMotion;
            MailNotifications.CreateChannel();
            NotificationService.Presenter = MailNotifications.Show;
            SoundService.Player = static soundName => _ = NotificationSoundPlayer.PlayAsync(soundName);
            MailSyncService.Start();
        }
    }
}
