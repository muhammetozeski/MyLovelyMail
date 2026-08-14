namespace MyLovelyMail.MainProject.Events
{
    public static class MainEvents
    {
        public static event Action<string, object?>? OnDataChanged;

        public static void Trigger(string eventName, object? data = null)
        {
            OnDataChanged?.Invoke(eventName, data);
        }
    }
}
