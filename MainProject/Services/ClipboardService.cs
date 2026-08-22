namespace MyLovelyMail.MainProject.Services
{
    /// <summary>
    /// Clipboard hook mirroring <see cref="SoundService.Player"/>: MainProject is a plain Razor
    /// library with no MAUI Essentials, so the platform head registers the actual writer.
    /// Null writer = platform without clipboard support; copying is then a no-op, never a crash.
    /// </summary>
    public static class ClipboardService
    {
        /// <summary>Registered by the head project (Windows: MAUI Clipboard). Null = no clipboard on this platform.</summary>
        public static Func<string, Task>? Writer;

        public static async Task CopyAsync(string text)
        {
            if (Writer == null || text.Length == 0) return;
            try
            {
                await Writer(text);
                Log($"Copied {text.Length} characters to the clipboard.");
            }
            catch (Exception ex)
            {
                Log($"Clipboard copy failed: {ex.Message}", LogLevel.Warning);
            }
        }
    }
}
