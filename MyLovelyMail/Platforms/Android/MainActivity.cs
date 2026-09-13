using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using MyLovelyMail.MainProject.Services;

namespace MyLovelyMail
{
    // A fixed Java name instead of the generated crc64 one, so adb and notification intents can address the activity by name.
    // Portrait only: with the display rotated to 270 while the phone lay still, the whole mail screen was drawn sideways.
    [Activity(Name = "com.muhammetozeski.mylovelymail.MainActivity", Theme = "@style/Maui.SplashTheme", MainLauncher = true, ScreenOrientation = ScreenOrientation.Portrait, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            MailNotifications.RequestPermission(this);
            MailNotifications.OpenFromIntent(Intent);
        }

        /// <summary>A notification tapped while the activity already exists arrives here instead of in OnCreate.</summary>
        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            MailNotifications.OpenFromIntent(intent);
        }
    }
}
