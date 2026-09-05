using System.Collections.Concurrent;
using System.Text.Json;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>How one account's syncing is going. A failing mailbox must not look like a quiet one.</summary>
    public sealed class SyncHealth
    {
        public DateTime? LastAttemptUtc { get; set; }
        public DateTime? LastSuccessUtc { get; set; }
        public DateTime? LastErrorUtc { get; set; }
        public string? LastErrorMessage { get; set; }
        public int ConsecutiveFailures { get; set; }

        /// <summary>
        /// How the last successful connection actually travelled — "direct", or the Tor endpoint
        /// and the ladder rung that carried it. Only the log knew this before, and a mailbox that
        /// only works on the third rung is a mailbox with a problem the user could not see.
        /// </summary>
        public string? LastRoute { get; set; }

        public DateTime? LastRouteUtc { get; set; }

        /// <summary>True while the last attempt is still unrecovered — what the UI paints red.</summary>
        public bool IsFailing => ConsecutiveFailures > 0;
    }

    /// <summary>
    /// Remembers per-account sync outcomes across restarts in <c>UserData/sync-health.json</c>.
    /// Every account failure funnels through SyncScheduler's per-account catch, so recording it
    /// there covers both protocols; the IDLE loop reports its drops too.
    /// </summary>
    public static class SyncHealthService
    {
        public const string HealthFileName = "sync-health.json";

        static string HealthPath => Path.Combine(AppPaths.UserData, HealthFileName);

        static readonly ConcurrentDictionary<string, SyncHealth> byAccountId = Load();

        public static event Action? OnHealthChanged;

        public static SyncHealth For(string accountId) => byAccountId.GetOrAdd(accountId, static _ => new SyncHealth());

        public static IReadOnlyDictionary<string, SyncHealth> All => byAccountId;

        public static void MarkAttempt(string accountId)
        {
            For(accountId).LastAttemptUtc = DateTime.UtcNow;
            Save();
        }

        public static void MarkSuccess(string accountId)
        {
            var health = For(accountId);
            health.LastSuccessUtc = DateTime.UtcNow;
            health.ConsecutiveFailures = 0;
            health.LastErrorMessage = null;
            Save();
        }

        /// <summary>
        /// Records how a connection just travelled. Written only when the route CHANGED: this runs
        /// on every connection an account makes — every sync pass, every IDLE reconnect — and
        /// rewriting the health file each time would be pure disk churn for an unchanged sentence.
        /// </summary>
        public static void MarkRoute(string accountId, string route)
        {
            var health = For(accountId);
            if (health.LastRoute == route) return;

            health.LastRoute = route;
            health.LastRouteUtc = DateTime.UtcNow;
            Save();
        }

        public static void MarkFailure(string accountId, string error)
        {
            var health = For(accountId);
            health.LastErrorUtc = DateTime.UtcNow;
            health.LastErrorMessage = error;
            health.ConsecutiveFailures++;
            Save();
        }

        static ConcurrentDictionary<string, SyncHealth> Load()
        {
            try
            {
                string path = Path.Combine(AppPaths.UserData, HealthFileName);
                if (!File.Exists(path)) return new();
                var stored = JsonSerializer.Deserialize<Dictionary<string, SyncHealth>>(File.ReadAllText(path), JsonDefaults.Indented);
                return stored == null ? new() : new(stored);
            }
            catch (Exception ex)
            {
                Log($"Could not read sync health: {ex.Message}", LogLevel.Warning);
                return new();
            }
        }

        static void Save()
        {
            try
            {
                AtomicFile.WriteAllText(HealthPath, JsonSerializer.Serialize(byAccountId, JsonDefaults.Indented));
            }
            catch (Exception ex)
            {
                Log($"Could not write sync health: {ex.Message}", LogLevel.Warning);
            }
            OnHealthChanged?.Invoke();
        }
    }
}
