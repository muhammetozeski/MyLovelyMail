using MyLovelyMail.MainProject.Services;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail
{
    /// <summary>
    /// Windows player for <see cref="SoundService"/>: bundled wav assets are extracted once into
    /// AppCache and played through a cached WinRT MediaPlayer. Unknown sound names fall back to
    /// the success sound; every failure logs and is swallowed — audio must never crash mail flow.
    /// </summary>
    public static class SoundBridge
    {
        public static void Initialize()
        {
#if WINDOWS
            SoundService.Player = soundName => _ = PlayAsync(soundName);
#endif
        }

#if WINDOWS
        static Windows.Media.Playback.MediaPlayer? cachedPlayer;

        static async Task PlayAsync(string soundName)
        {
            try
            {
                // The user rejected the old long success.wav outright — nothing maps to it anymore.
                string fileName = soundName.ToLowerInvariant() switch
                {
                    "spin" => "spin.wav",
                    _ => "notify.wav"
                };

                string cachedPath = Path.Combine(AppPaths.AppCache, "Sounds", fileName);
                if (!File.Exists(cachedPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
                    using var packaged = await FileSystem.OpenAppPackageFileAsync($"Sounds/{fileName}");
                    await using var output = File.Create(cachedPath);
                    await packaged.CopyToAsync(output);
                }

                cachedPlayer ??= new Windows.Media.Playback.MediaPlayer();
                cachedPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(cachedPath));
                cachedPlayer.Play();
            }
            catch (Exception ex)
            {
                Logger.Log($"Sound playback failed ({soundName}): {ex.Message}", Logger.LogLevel.Warning);
            }
        }
#endif
    }
}
