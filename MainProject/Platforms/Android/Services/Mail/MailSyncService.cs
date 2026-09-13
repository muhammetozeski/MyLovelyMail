using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using MyLovelyMail.MainProject.Constants;
using SystemResource = Android.Resource;

[assembly: UsesPermission(Manifest.Permission.ForegroundService)]
[assembly: UsesPermission(Manifest.Permission.ForegroundServiceSpecialUse)]

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Keeps the app's process at foreground-service priority, so the sync loop and the IMAP IDLE
    /// connections MauiProgram started keep running after the user leaves the app; a plain background
    /// process is frozen or killed by Android within minutes. The service does no mail work itself.
    /// <para>
    /// The type is specialUse, not dataSync: Android 15 caps dataSync at six hours a day and does not
    /// let a boot broadcast start it, while a mail client has to stay connected all day from boot on.
    /// </para>
    /// </summary>
    [Service(Name = "com.muhammetozeski.mylovelymail.MailSyncService", Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
    public class MailSyncService : Service
    {
        const string ChannelId = "background-sync";
        const string ChannelName = "Background mail check";
        const string NotificationText = "Checking for new mail in the background";
        const int NotificationId = 1;

        /// <summary>Starts the service; a call while it already runs only repeats OnStartCommand.</summary>
        public static void Start()
        {
            var context = Application.Context;
            context.StartForegroundService(new Intent(context, typeof(MailSyncService)));
        }

        public override IBinder? OnBind(Intent? intent) => null;

        /// <summary>Sticky: when Android kills the process for memory, it restarts the service and with it the app's sync loop.</summary>
        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            StartForeground(NotificationId, BuildNotification(), ForegroundService.TypeSpecialUse);
            Log("Mail sync service is running in the foreground.");
            return StartCommandResult.Sticky;
        }

        /// <summary>The ongoing notification Android requires for a foreground service; a tap opens the app.</summary>
        Notification BuildNotification()
        {
            var manager = (NotificationManager)GetSystemService(NotificationService)!;
            // Low importance: listed in the shade without a sound, a pop-up or a status-bar icon of its own.
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low));

            var openApp = PendingIntent.GetActivity(this, NotificationId,
                PackageManager!.GetLaunchIntentForPackage(PackageName!), PendingIntentFlags.Immutable);

            return new Notification.Builder(this, ChannelId)
                .SetSmallIcon(SystemResource.Drawable.StatNotifySync)
                .SetContentTitle(AppConstants.AppNameHumanReadable)
                .SetContentText(NotificationText)
                .SetOngoing(true)
                .SetContentIntent(openApp)
                .Build();
        }
    }
}
