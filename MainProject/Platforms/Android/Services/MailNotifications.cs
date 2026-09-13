using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using MyLovelyMail.MainProject.Storage;
using SystemResource = Android.Resource;

[assembly: UsesPermission(Manifest.Permission.PostNotifications)]

namespace MyLovelyMail.MainProject.Services
{
    /// <summary>
    /// New-mail notifications on Android. One channel with no sound of its own — the sound a rule picked
    /// is played by <see cref="NotificationService"/> through <see cref="SoundService"/>, exactly as the
    /// Windows toast is muted — one notification per message, and a tap that opens that message.
    /// </summary>
    public static class MailNotifications
    {
        const string ChannelId = "new-mail";
        const string ChannelName = "New mail";
        const string AccountIdExtra = "mylovelymail.accountId";
        const string FolderExtra = "mylovelymail.folder";
        const string UidExtra = "mylovelymail.uid";
        const int PermissionRequestCode = 4201;

        static NotificationManager Manager =>
            (NotificationManager)Application.Context.GetSystemService(Context.NotificationService)!;

        /// <summary>Creates the new-mail channel. Android keeps a channel once created, so calling this at every start only costs the first time.</summary>
        public static void CreateChannel()
        {
            var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.High);
            channel.SetSound(null, null);
            Manager.CreateNotificationChannel(channel);
        }

        /// <summary>
        /// Asks for the notification permission Android 13 and newer require. Without it every
        /// notification below is dropped silently, so the activity asks once at start; Android itself
        /// stops showing the prompt after the user has answered it.
        /// </summary>
        /// <param name="activity">The visible activity the system dialog attaches to.</param>
        public static void RequestPermission(Activity activity)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
            if (activity.CheckSelfPermission(Manifest.Permission.PostNotifications) == Permission.Granted) return;
            activity.RequestPermissions([Manifest.Permission.PostNotifications], PermissionRequestCode);
        }

        /// <summary>The Android <see cref="NotificationService.Presenter"/>.</summary>
        /// <param name="toast">What to show and which message a tap opens.</param>
        public static void Show(MailToast toast)
        {
            var context = Application.Context;
            // The same message always maps to the same id, so a repeated notification replaces the old one instead of stacking.
            int notificationId = unchecked((int)StableHash.Fnv1a($"{toast.AccountId}\n{toast.FolderFullName}\n{toast.Uid}"));

            var notification = new Notification.Builder(context, ChannelId)
                .SetSmallIcon(SystemResource.Drawable.SymActionEmail)
                .SetContentTitle(toast.Title)
                .SetContentText(toast.Body)
                .SetStyle(new Notification.BigTextStyle().BigText(toast.Body))
                .SetCategory(Notification.CategoryEmail)
                .SetAutoCancel(true)
                .SetContentIntent(OpenMessageIntent(context, toast, notificationId))
                .Build();

            Manager.Notify(notificationId, notification);
            Log($"Notification shown: {toast.Title} — {toast.Body}");
        }

        /// <summary>
        /// Opens the message a tapped notification carried. The activity passes the intent that started it
        /// and every later one; an ordinary launch carries none of the extras and changes nothing.
        /// </summary>
        /// <param name="intent">The intent the activity received.</param>
        public static void OpenFromIntent(Intent? intent)
        {
            if (intent?.GetStringExtra(AccountIdExtra) is not { } accountId
                || intent.GetStringExtra(FolderExtra) is not { } folderFullName)
                return;

            NotificationService.OpenNotifiedMessage(accountId, folderFullName, (uint)intent.GetLongExtra(UidExtra, 0));
        }

        /// <summary>
        /// Brings the existing activity forward (SingleTop + ClearTop, so it is not stacked a second
        /// time) with the message's identity attached. The request code is the notification id, which
        /// keeps every notification's extras apart.
        /// </summary>
        static PendingIntent OpenMessageIntent(Context context, MailToast toast, int requestCode)
        {
            var intent = context.PackageManager!.GetLaunchIntentForPackage(context.PackageName!)!
                .AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop)
                .PutExtra(AccountIdExtra, toast.AccountId)
                .PutExtra(FolderExtra, toast.FolderFullName)
                .PutExtra(UidExtra, (long)toast.Uid);

            return PendingIntent.GetActivity(context, requestCode, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
        }
    }
}
