using System.Text.Json;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>
    /// The Message-Ids of conversations the user has muted, persisted as
    /// <c>UserData/muted-threads.json</c>.
    /// <para>
    /// Mute has to outlive the messages it was applied to: a reply arrives with a NEW uid, and
    /// per-message state only carries across the same uid, so a flag on today's messages says
    /// nothing about tomorrow's. Message-Ids are what a reply actually points back at.
    /// </para>
    /// <para>
    /// Loads on first use rather than from a startup call, so there is no fourth entry point to
    /// keep in step with MauiProgram, the Web host and MigrationService.
    /// </para>
    /// </summary>
    public static class MutedThreadStore
    {
        public const string FileName = "muted-threads.json";

        static HashSet<string>? mutedMessageIds;

        static string StorePath => Path.Combine(AppPaths.UserData, FileName);

        static HashSet<string> Ids
        {
            get
            {
                if (mutedMessageIds != null) return mutedMessageIds;
                mutedMessageIds = new(StringComparer.OrdinalIgnoreCase);
                if (!File.Exists(StorePath)) return mutedMessageIds;
                try
                {
                    if (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StorePath)) is { } loaded)
                        mutedMessageIds = new(loaded, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    Log($"Corrupt muted-threads file, starting empty: {ex.Message}", LogLevel.Warning);
                }
                return mutedMessageIds;
            }
        }

        public static IReadOnlyCollection<string> All => Ids;

        /// <summary>True when any of these ids belongs to a muted conversation.</summary>
        public static bool ContainsAny(IEnumerable<string> messageIds) =>
            messageIds.Any(id => id.Length > 0 && Ids.Contains(id));

        public static void Add(IEnumerable<string> messageIds)
        {
            int before = Ids.Count;
            foreach (string id in messageIds.Where(static id => id.Length > 0)) Ids.Add(id);
            if (Ids.Count != before) Persist();
        }

        public static void Remove(IEnumerable<string> messageIds)
        {
            int before = Ids.Count;
            foreach (string id in messageIds) Ids.Remove(id);
            if (Ids.Count != before) Persist();
        }

        static void Persist()
        {
            try
            {
                AtomicFile.WriteAllText(StorePath, JsonSerializer.Serialize(Ids, JsonDefaults.Indented));
            }
            catch (Exception ex)
            {
                Log($"Could not save the muted threads: {ex.Message}", LogLevel.Warning);
            }
        }
    }
}
