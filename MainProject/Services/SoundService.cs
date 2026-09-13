using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services
{
    /// <summary>
    /// Sound hook mirroring <see cref="NotificationService.Presenter"/>: MainProject requests a
    /// sound by name, the platform head registers the actual player. Null player or the "none"
    /// name silences; playback failures must be handled inside the player, never thrown here.
    /// </summary>
    public static class SoundService
    {
        /// <summary>Registered by the head project (Windows: MediaPlayer bridge, Android: NotificationSoundPlayer). Null = silent platform.</summary>
        public static Action<string>? Player;

        public static void Play(string soundName)
        {
            if (Player == null || string.IsNullOrWhiteSpace(soundName)) return;
            if (soundName.Equals(NotificationService.SilentSoundName, StringComparison.OrdinalIgnoreCase)) return;
            Log($"Sound requested: {soundName}");
            Player(soundName);
        }

        /// <summary>
        /// The bundled wav for a sound name, copied out of the app package into AppCache on first use,
        /// because both platform players open a plain file rather than a package entry.
        /// </summary>
        /// <param name="soundName">The requested sound; a name with no file of its own gets the notification sound.</param>
        /// <param name="openPackagedFile">Opens a file inside the app package by its package path, e.g. "Sounds/notify.wav".</param>
        /// <returns>The full path of the cached file.</returns>
        public static async Task<string> ExtractToCacheAsync(string soundName, Func<string, Task<Stream>> openPackagedFile)
        {
            // The user rejected the old long success.wav outright — nothing maps to it anymore.
            string fileName = soundName.ToLowerInvariant() switch
            {
                "spin" => "spin.wav",
                _ => "notify.wav"
            };

            string cachedPath = Path.Combine(AppPaths.AppCache, "Sounds", fileName);
            if (File.Exists(cachedPath)) return cachedPath;

            // Copied under a temporary name first: a copy cut short must not leave a file that
            // File.Exists above would accept on every later play.
            string partialPath = cachedPath + ".partial";
            Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
            await using (var packaged = await openPackagedFile($"Sounds/{fileName}"))
            await using (var output = File.Create(partialPath))
                await packaged.CopyToAsync(output);
            File.Move(partialPath, cachedPath, overwrite: true);
            return cachedPath;
        }
    }
}
