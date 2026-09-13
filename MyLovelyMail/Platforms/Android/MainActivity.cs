using Android.App;
using Android.Content.PM;
using Android.OS;

namespace MyLovelyMail
{
    // A fixed Java name instead of the generated crc64 one, so adb and notification intents can address the activity by name.
    // Portrait only: with the display rotated to 270 while the phone lay still, the whole mail screen was drawn sideways.
    [Activity(Name = "com.muhammetozeski.mylovelymail.MainActivity", Theme = "@style/Maui.SplashTheme", MainLauncher = true, ScreenOrientation = ScreenOrientation.Portrait, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
    }
}
