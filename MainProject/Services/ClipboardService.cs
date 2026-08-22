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

        /// <summary>
        /// True only when the text really reached the clipboard. The caller needs the answer: a
        /// "copied" line shown over a failed write is worse than no line at all, because the user
        /// then pastes whatever was in the clipboard before.
        /// </summary>
        public static async Task<bool> CopyAsync(string text)
        {
            if (Writer == null || text.Length == 0) return false;
            try
            {
                await Writer(text);
                Log($"Copied {text.Length} characters to the clipboard.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Clipboard copy failed: {ex.Message}", LogLevel.Warning);
                return false;
            }
        }
    }
}
