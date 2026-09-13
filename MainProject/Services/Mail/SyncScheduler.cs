using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Stores;

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
                    await WaitBetweenPasses(TimeSpan.FromMinutes(minutes), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>Syncs every enabled account once. Safe to call while the loop runs.</summary>
        public static async Task SyncNowAsync(CancellationToken cancellationToken = default)
        {
            if (passRunning) return;
            passRunning = true;
            // The finally is load-bearing: any exception escaping this body would otherwise leave
            // passRunning stuck at true and silently kill every future sync pass.
            try
            {
                // Cheap safety net: picks up UseImapIdle toggles (global or per-account) within one
                // polling cycle even though nothing explicitly notifies this scheduler about them.
                ImapIdleService.Refresh();

                foreach (var account in AccountStore.Accounts.Where(a => a.Enabled))
                {
                    try
                    {
                        SyncHealthService.MarkAttempt(account.Id);
                        if (account.Protocol == IncomingProtocol.Imap)
                            await ImapSyncService.SyncAccountAsync(account, cancellationToken);
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
                        Log($"Sync failed for '{account.EmailAddress}': {ex.Message}", LogLevel.Error);
                    }
                }

                // A completed pass is the online signal — retry anything parked in the Outboxes.
                try
                {
                    await OutboxService.FlushAsync(cancellationToken);
                }
                catch (OperationCanceledException) { }

                SnoozeService.WakeDue();
                OfflineCacheTrimmer.TrimAll();
            }
            finally
            {
                passRunning = false;
            }
            Log("Sync pass finished for all enabled accounts.");
        }
    }
}
