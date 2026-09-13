using MyLovelyMail.MainProject.Services;
#if WINDOWS
using Windows.Media.Core;
using Windows.Media.Playback;
#endif

namespace MyLovelyMail
{
    /// <summary>
    /// Windows player for <see cref="SoundService"/>: the bundled wav that <see cref="SoundService.ExtractToCacheAsync"/>
    /// copies into AppCache, played through a cached WinRT MediaPlayer. Every failure logs and is
    /// swallowed — audio must never crash mail flow.
    /// </summary>
    public static class SoundBridge
    {
        public static void Initialize()
        {
#if WINDOWS
            SoundService.Player = static soundName => _ = PlayAsync(soundName);
#endif
        }

#if WINDOWS
        static MediaPlayer? cachedPlayer;

        static async Task PlayAsync(string soundName)
        {
            try
            {
                string cachedPath = await SoundService.ExtractToCacheAsync(soundName, static packagePath => FileSystem.OpenAppPackageFileAsync(packagePath));

                cachedPlayer ??= new MediaPlayer();
                cachedPlayer.Source = MediaSource.CreateFromUri(new Uri(cachedPath));
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
