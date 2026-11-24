namespace MyLovelyMail.MainProject.Storage
{
    /// <summary>
    /// Asks the user to choose an existing file through the operating system's own picker. On
    /// Android this is the only way the app may read a file it did not create, such as a migration
    /// bundle copied into the phone's Download folder. MainProject has no MAUI Essentials, so the
    /// native head registers <see cref="Picker"/>, as it does for
    /// <see cref="Services.ClipboardService.Writer"/>; the web preview leaves it null and offers no picker.
    /// </summary>
    public static class UserFilePicker
    {
        /// <summary>
        /// Registered by the MAUI head. Shows the picker and returns a path the app can open with
        /// ordinary file APIs, or null when the user closes it without choosing.
        /// </summary>
        public static Func<Task<string?>>? Picker;

        /// <summary>True when this platform registered a picker.</summary>
        public static bool IsAvailable => Picker != null;

        /// <summary>
        /// Shows the picker and returns the chosen file's readable path; on Android that is the private
        /// copy MAUI makes of the chosen document. Null when no picker is registered or the user
        /// cancelled. Picker failures propagate so the calling action can report them.
        /// </summary>
        public static async Task<string?> PickAsync()
        {
            if (Picker == null) return null;
            string? path = await Picker();
            Log(path == null ? "File picker closed without a choice." : $"File picked: {Path.GetFileName(path)}");
            return path;
        }
    }
}
