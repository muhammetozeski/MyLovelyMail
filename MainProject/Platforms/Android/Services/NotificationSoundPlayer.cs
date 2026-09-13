using Android.App;
using Android.Media;

namespace MyLovelyMail.MainProject.Services
{
    /// <summary>
    /// The Android <see cref="SoundService.Player"/>: the bundled wav played by a MediaPlayer tagged as a
    /// notification sound, so it follows the phone's notification volume rather than the media volume.
    /// </summary>
    public static class NotificationSoundPlayer
    {
        /// <summary>Plays one sound; a failure is logged and swallowed, because audio must never break mail flow.</summary>
        /// <param name="soundName">The sound a rule or the account chose.</param>
        public static async Task PlayAsync(string soundName)
        {
            try
            {
                string path = await SoundService.ExtractToCacheAsync(soundName,
                    static packagePath => Task.FromResult(Application.Context.Assets!.Open(packagePath)));

                var player = new MediaPlayer();
                player.SetAudioAttributes(new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.Notification)!
                    .SetContentType(AudioContentType.Sonification)!
                    .Build()!);
                player.SetDataSource(path);
                // One player per sound, released when it finishes: two notifications in a row must not cut each other off.
                player.Completion += (_, _) => player.Release();
                player.Prepare();
                player.Start();
            }
            catch (Exception ex)
            {
                Log($"Sound playback failed ({soundName}): {ex.Message}", LogLevel.Warning);
            }
        }
    }
}
