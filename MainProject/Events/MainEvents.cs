namespace MyLovelyMail.MainProject.Events
{
    /// <summary>
    /// App-wide broadcast pipe. <see cref="Trigger"/> raises <see cref="OnDataChanged"/> with a
    /// string event name; subscribers filter by that name (currently "OnThemeChanged" and
    /// "OnStyleChanged", see ThemedComponentBase).
    /// </summary>
    public static class MainEvents
    {
        public static event Action<string, object?>? OnDataChanged;

        /// <summary>Notifies every <see cref="OnDataChanged"/> subscriber.</summary>
        /// <param name="eventName">Name the subscribers filter on, e.g. "OnThemeChanged".</param>
        /// <param name="data">Optional payload; the current events all pass none.</param>
        public static void Trigger(string eventName, object? data = null) => OnDataChanged?.Invoke(eventName, data);
    }
}
