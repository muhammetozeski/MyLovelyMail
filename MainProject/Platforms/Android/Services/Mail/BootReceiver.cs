using Android;
using Android.App;
using Android.Content;

[assembly: UsesPermission(Manifest.Permission.ReceiveBootCompleted)]

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Starts the mail sync service after the phone boots and after the app is updated. Android creates
    /// the app's process to deliver the broadcast, and MauiProgram starts the sync loop inside it; this
    /// receiver only moves that process into the foreground service so it is not frozen again.
    /// <para>
    /// BOOT_COMPLETED, not LOCKED_BOOT_COMPLETED: before the first unlock the app's files are still
    /// encrypted, and the settings and the vault could not be read.
    /// </para>
    /// </summary>
    [BroadcastReceiver(Name = "com.muhammetozeski.mylovelymail.BootReceiver", Exported = true)]
    [IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced])]
    public class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            Log($"{intent?.Action} received; starting the mail sync service.");
            MailSyncService.Start();
        }
    }
}
