using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Stores;
using System.Security.Cryptography;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Background sync loop: every <see cref="Settings.SyncIntervalMinutes"/> it syncs all enabled
    /// accounts (IMAP or POP3 by account protocol). Failures of one account never block another —
    /// each sync is isolated and logged. <see cref="SyncNowAsync"/> serves the manual refresh button.
    /// </summary>
    public static class SyncScheduler
    {
        static CancellationTokenSource? loopCancellation;
        static int activeSyncCount;
        static bool passRunning;

        /// <summary>True while ANY sync runs — scheduled pass, IDLE push or folder-on-open alike.</summary>
        public static bool IsSyncing => Volatile.Read(ref activeSyncCount) > 0;

        public static event Action? OnSyncStateChanged;

        /// <summary>
        /// How the loop waits between two passes. A plain delay by default; Android replaces it with an
        /// alarm, because a delay cannot wake a sleeping phone and the next pass would wait for whatever
        /// woke it next.
        /// </summary>
        public static Func<TimeSpan, CancellationToken, Task> WaitBetweenPasses = static (interval, cancellationToken) => Task.Delay(interval, cancellationToken);

        /// <summary>
        /// Counted activity scope. Each real sync (account pass, single folder) wraps itself in
        /// this, so the heart/sweep indicators fire no matter WHO triggered the sync. Without the
        /// counter, IDLE-triggered and folder-open syncs ran invisible.
        /// </summary>
        internal static IDisposable EnterSyncScope() => new SyncScope();

        sealed class SyncScope : IDisposable
        {
            public SyncScope()
            {
                Interlocked.Increment(ref activeSyncCount);
                OnSyncStateChanged?.Invoke();
            }

            public void Dispose()
            {
                Interlocked.Decrement(ref activeSyncCount);
                OnSyncStateChanged?.Invoke();
            }
        }

        /// <summary>Starts the periodic loop. Idempotent — extra calls are ignored while it runs.</summary>
        public static void Start()
        {
            if (loopCancellation != null) return;
            loopCancellation = new CancellationTokenSource();
            _ = LoopAsync(loopCancellation.Token);
        }

        static async Task LoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await SyncNowAsync(cancellationToken);

                int minutes = Math.Max(1, Settings.SyncIntervalMinutes.Value);
                try
                {
                    double factor = AccountStore.Accounts.Any(static a => a.Enabled && a.TorOnly)
                        ? RandomNumberGenerator.GetInt32(700, 1301) / 1000.0 : 1;
                    await WaitBetweenPasses(TimeSpan.FromMinutes(minutes * factor), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>Syncs every enabled account once. Safe to call while the loop runs.</summary>
        public static async Task SyncNowAsync(CancellationToken cancellationToken = default, string? accountId = null)
        {
            if (passRunning) return;
            passRunning = true;
            // The finally is load-bearing: any exception escaping this body would otherwise leave
            // passRunning stuck at true and silently kill every future sync pass.
            try
            {
                // Cheap safety net: picks up UseImapIdle toggles (global or per-account) within one
                // polling cycle even though nothing explicitly notifies this scheduler about them.
                if (accountId == null) ImapIdleService.Refresh();

                var accounts = AccountStore.Accounts.Where(account => accountId == null ? account.Enabled : account.Id == accountId).ToArray();
                if (accountId == null)
                {
                    var anonymous = accounts.Where(static a => a.TorOnly).ToArray();
                    RandomNumberGenerator.Shuffle(anonymous.AsSpan());
                    int index = 0;
                    for (int i = 0; i < accounts.Length; i++)
                        if (accounts[i].TorOnly) accounts[i] = anonymous[index++];
                }
                foreach (var account in accounts)
                {
                    try
                    {
                        if (accountId == null && account.TorOnly)
                            await Task.Delay(RandomNumberGenerator.GetInt32(1000, 5001), cancellationToken);
                        SyncHealthService.MarkAttempt(account.Id);
                        if (account.Protocol == IncomingProtocol.Imap)
                            await ImapSyncService.SyncAccountAsync(account, cancellationToken);
                        else if (account.Protocol == IncomingProtocol.FastMail)
                            await FastMail.FastMailSyncService.SyncAccountAsync(account, cancellationToken);
                        else
                            await Pop3Service.SyncAccountAsync(account, cancellationToken);
                        SyncHealthService.MarkSuccess(account.Id);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        SyncHealthService.MarkFailure(account.Id, ex.Message);
                        Log($"Sync failed for '{account.EmailAddress}': {ex}", LogLevel.Error);
                    }
                }

                if (accountId == null)
                {
                    // A completed global pass also retries Outboxes and maintains the local cache.
                    try { await OutboxService.FlushAsync(cancellationToken); }
                    catch (OperationCanceledException) { }
                    SnoozeService.WakeDue();
                    OfflineCacheTrimmer.TrimAll();
                }
            }
            finally
            {
                passRunning = false;
            }
            Log(accountId == null ? "Sync pass finished for all enabled accounts." : $"Sync pass finished for account {accountId}.");
        }
    }
}
