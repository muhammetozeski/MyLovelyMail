namespace MyLovelyMail.MainProject.Services
{
    /// <summary>
    /// Sound hook mirroring <see cref="NotificationService.Presenter"/>: MainProject requests a
    /// sound by name, the platform head registers the actual player. Null player or the "none"
    /// name silences; playback failures must be handled inside the player, never thrown here.
    /// </summary>
    public static class SoundService
    {
        /// <summary>Registered by the head project (Windows: MediaPlayer bridge). Null = silent platform.</summary>
        public static Action<string>? Player;

        public static void Play(string soundName)
        {
            if (Player == null || string.IsNullOrWhiteSpace(soundName)) return;
            if (soundName.Equals(NotificationService.SilentSoundName, StringComparison.OrdinalIgnoreCase)) return;
            Log($"Sound requested: {soundName}");
            Player(soundName);
        }
    }
}
