using System.Text.Json;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>What one trim pass did, or would have done when it was a dry run.</summary>
    public sealed class TrimReport
    {
        public DateTime RanAtUtc { get; set; }
        public bool WasDryRun { get; set; }
        public long BytesFreed { get; set; }
        public int BodiesDropped { get; set; }

        /// <summary>Bytes per "account e-mail / folder", so a single heavy folder is visible instead of averaged away.</summary>
        public Dictionary<string, long> ByFolder { get; set; } = [];

        /// <summary>Accounts skipped because neither a keep-days nor a size budget was set.</summary>
        public List<string> SkippedAccounts { get; set; } = [];
    }

    /// <summary>
    /// Keeps the offline body cache inside its budget. Two stages, both dropping only the cached
    /// .eml file and never the summary, so rows survive and a body downloads again when opened:
    /// first age (<see cref="AccountSettings.OfflineKeepDays"/>), then size
    /// (<see cref="AccountSettings.OfflineMaxCacheMb"/>) dropping oldest-first until the account
    /// fits. Age alone was the wrong axis: one folder of attachments outweighs a year of text.
    /// <para>
    /// Every pass writes a receipt to <c>UserData/cache-trim.json</c>. Before, a trimmer that found
    /// nothing, one disabled by the default of 0 and one that never ran all produced identical
    /// output — nothing — so there was no way to tell them apart.
    /// </para>
    /// </summary>
    public static class OfflineCacheTrimmer
    {
        public const string ReportFileName = "cache-trim.json";

        const long BytesPerMegabyte = 1024 * 1024;

        static string ReportPath => Path.Combine(AppPaths.UserData, ReportFileName);

        static TrimReport? lastReport;

        /// <summary>The most recent pass, read from disk on first access; null before the first ever run.</summary>
        public static TrimReport? LastReport
        {
            get
            {
                if (lastReport != null) return lastReport;
                try
                {
                    if (File.Exists(ReportPath))
                        lastReport = JsonSerializer.Deserialize<TrimReport>(File.ReadAllText(ReportPath));
                }
                catch (Exception ex)
                {
                    Log($"Could not read the last cache-trim report: {ex.Message}", LogLevel.Warning);
                }
                return lastReport;
            }
        }

        /// <summary>One cached body and what dropping it would reclaim.</summary>
        readonly record struct CachedBody(string FolderFullName, uint Uid, DateTime DateUtc, long Bytes);

        /// <summary>
        /// Runs both stages over every account. <paramref name="dryRun"/> measures identically but
        /// deletes nothing, so the app can say "would free 240 MB" before anything is destroyed.
        /// </summary>
        public static TrimReport TrimAll(bool dryRun = false)
        {
            var report = new TrimReport { RanAtUtc = DateTime.UtcNow, WasDryRun = dryRun };

            foreach (var account in AccountStore.Accounts)
            {
                var settings = AccountStore.GetSettings(account.Id);
                int keepDays = settings.OfflineKeepDays.Value;
                long budgetBytes = (long)settings.OfflineMaxCacheMb.Value * BytesPerMegabyte;

                if (keepDays <= 0 && budgetBytes <= 0)
                {
                    report.SkippedAccounts.Add(account.EmailAddress);
                    continue;
                }

                var bodies = MeasureCachedBodies(account.Id);

                if (keepDays > 0)
                {
                    var cutoffUtc = DateTime.UtcNow.AddDays(-keepDays);
                    foreach (var body in bodies.Where(b => b.DateUtc < cutoffUtc).ToList())
                    {
                        Drop(account, body, report, dryRun);
                        bodies.Remove(body);
                    }
                }

                if (budgetBytes > 0)
                {
                    long total = bodies.Sum(static b => b.Bytes);
                    // Oldest first: the newest mail is the mail most likely to be opened again.
                    foreach (var body in bodies.OrderBy(static b => b.DateUtc))
                    {
                        if (total <= budgetBytes) break;
                        Drop(account, body, report, dryRun);
                        total -= body.Bytes;
                    }
                }
            }

            Log($"Offline trim {(dryRun ? "dry run" : "pass")}: {report.BodiesDropped} bodies, "
                + $"{report.BytesFreed / BytesPerMegabyte} MB across {report.ByFolder.Count} folder(s); "
                + $"{report.SkippedAccounts.Count} account(s) had no budget set.");

            if (!dryRun) Persist(report);
            return report;
        }

        /// <summary>Every cached body of an account with its size; summaries whose body is not on disk are skipped.</summary>
        static List<CachedBody> MeasureCachedBodies(string accountId)
        {
            var bodies = new List<CachedBody>();
            foreach (var folder in MessageStore.GetFolders(accountId))
            {
                foreach (var summary in MessageStore.GetSummaries(accountId, folder.FullName))
                {
                    var file = new FileInfo(MessageStore.MessagePath(accountId, folder.FullName, summary.Uid));
                    if (file.Exists)
                        bodies.Add(new CachedBody(folder.FullName, summary.Uid, summary.DateUtc, file.Length));
                }
            }
            return bodies;
        }

        static void Drop(MailAccountData account, CachedBody body, TrimReport report, bool dryRun)
        {
            if (!dryRun && !MessageStore.DeleteCachedBody(account.Id, body.FolderFullName, body.Uid)) return;

            report.BodiesDropped++;
            report.BytesFreed += body.Bytes;
            string key = $"{account.EmailAddress} / {body.FolderFullName}";
            report.ByFolder[key] = report.ByFolder.GetValueOrDefault(key) + body.Bytes;
        }

        static void Persist(TrimReport report)
        {
            lastReport = report;
            try
            {
                AtomicFile.WriteAllText(ReportPath, JsonSerializer.Serialize(report, JsonDefaults.Indented));
            }
            catch (Exception ex)
            {
                Log($"Could not write the cache-trim report: {ex.Message}", LogLevel.Warning);
            }
        }
    }
}
