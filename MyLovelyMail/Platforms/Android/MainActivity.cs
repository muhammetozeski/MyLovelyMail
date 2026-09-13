using Android.App;
using Android.Content.PM;
using Android.OS;

namespace MyLovelyMail
{
    // A fixed Java name instead of the generated crc64 one, so adb and notification intents can address the activity by name.
    [Activity(Name = "com.muhammetozeski.mylovelymail.MainActivity", Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
    }
}
