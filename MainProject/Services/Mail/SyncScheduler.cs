using System.Collections.Concurrent;
using System.Security.Cryptography;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Background sync loop: every <see cref="Settings.SyncIntervalMinutes"/> it syncs all enabled
    /// accounts (IMAP, FastMail, or POP3). Direct accounts run in parallel without ever blocking on
    /// slower Tor-only accounts, and per-account gates ensure manual syncs are never silently dropped.
    /// </summary>
    public static class SyncScheduler
    {
        const int MaxDirectConcurrency = 4;
        const int MaxTorConcurrency = 2;

        static CancellationTokenSource? loopCancellation;
        static int activeSyncCount;
        static int globalPassRunning;
        static long nextScopeId;

        static readonly ConcurrentDictionary<long, (string? AccountId, string Label)> activeScopes = new();
        static readonly ConcurrentDictionary<string, SemaphoreSlim> accountGates = new(StringComparer.Ordinal);

        /// <summary>True while ANY sync runs — scheduled pass, IDLE push or folder-on-open alike.</summary>
        public static bool IsSyncing => Volatile.Read(ref activeSyncCount) > 0;

        /// <summary>True when the given account currently has an active sync operation in flight.</summary>
        public static bool IsAccountSyncing(string accountId) =>
            !string.IsNullOrWhiteSpace(accountId)
            && activeScopes.Values.Any(s => string.Equals(s.AccountId, accountId, StringComparison.Ordinal));

        /// <summary>Human-readable summary of what is actively syncing right now, or empty when idle.</summary>
        public static string ActiveSyncSummary
        {
            get
            {
                string[] labels = [.. activeScopes.Values
                    .Select(static s => s.Label)
                    .Where(static l => !string.IsNullOrWhiteSpace(l))
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
                return labels.Length switch
                {
                    0 => IsSyncing ? "Checking mail…" : string.Empty,
                    1 => $"Checking {labels[0]}…",
                    2 => $"Checking {labels[0]} & {labels[1]}…",
                    _ => $"Checking {labels[0]} (+{labels.Length - 1} more)…"
                };
            }
        }

        public static event Action? OnSyncStateChanged;

        /// <summary>
        /// How the loop waits between two passes. A plain delay by default; Android replaces it with an
        /// alarm, because a delay cannot wake a sleeping phone and the next pass would wait for whatever
        /// woke it next.
        /// </summary>
        public static Func<TimeSpan, CancellationToken, Task> WaitBetweenPasses = static (interval, cancellationToken) => Task.Delay(interval, cancellationToken);

        /// <summary>
        /// Counted activity scope. Each real sync (account pass, single folder) wraps itself in
        /// this, so the heart/sweep indicators fire no matter WHO triggered the sync.
        /// </summary>
        internal static IDisposable EnterSyncScope(string? accountId = null, string? label = null) =>
            new SyncScope(accountId, label ?? string.Empty);

        sealed class SyncScope : IDisposable
        {
            readonly long scopeId;

            public SyncScope(string? accountId, string label)
            {
                scopeId = Interlocked.Increment(ref nextScopeId);
                activeScopes[scopeId] = (accountId, label);
                Interlocked.Increment(ref activeSyncCount);
                OnSyncStateChanged?.Invoke();
            }

            public void Dispose()
            {
                activeScopes.TryRemove(scopeId, out _);
                Interlocked.Decrement(ref activeSyncCount);
                OnSyncStateChanged?.Invoke();
            }
        }

        /// <summary>Starts the periodic loop. Idempotent — extra calls are ignored while it runs.</summary>
        public static void Start()
        {
            if (loopCancellation != null) return;
            try { TrashRetentionService.RunMaintenance(); }
            catch (Exception ex) { Log($"Initial Trash retention pass failed: {ex.Message}", LogLevel.Warning); }
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

        /// <summary>
        /// Syncs every enabled account (or one targeted account) concurrently. Direct accounts run immediately
        /// without waiting behind Tor-only accounts, and targeted single-account syncs are never blocked by a
        /// global pass running on other accounts.
        /// </summary>
        public static async Task SyncNowAsync(CancellationToken cancellationToken = default, string? accountId = null)
        {
            if (accountId != null)
            {
                var target = AccountStore.Accounts.FirstOrDefault(a => a.Id == accountId);
                if (target != null)
                    await SyncSingleAccountIsolatedAsync(target, staggeredTorDelay: false, cancellationToken);
                Log($"Sync pass finished for account {accountId}.");
                return;
            }

            if (Interlocked.CompareExchange(ref globalPassRunning, 1, 0) != 0)
                return;

            try
            {
                // Cheap safety net: picks up UseImapIdle toggles (global or per-account) within one
                // polling cycle even though nothing explicitly notifies this scheduler about them.
                ImapIdleService.Refresh();

                var accounts = AccountStore.Accounts.Where(static account => account.Enabled).ToArray();
                var directAccounts = accounts.Where(static a => !a.TorOnly).ToArray();
                var torAccounts = accounts.Where(static a => a.TorOnly).ToArray();
                RandomNumberGenerator.Shuffle(torAccounts.AsSpan());

                Task directTask = RunPoolAsync(directAccounts, MaxDirectConcurrency, staggeredTorDelay: false, cancellationToken);
                Task torTask = RunPoolAsync(torAccounts, MaxTorConcurrency, staggeredTorDelay: true, cancellationToken);
                await Task.WhenAll(directTask, torTask);

                // A completed global pass also retries Outboxes and maintains the local cache.
                try { await OutboxService.FlushAsync(cancellationToken); }
                catch (OperationCanceledException) { }
                SnoozeService.WakeDue();
                OfflineCacheTrimmer.TrimAll();
                TrashRetentionService.RunMaintenance();
            }
            finally
            {
                Volatile.Write(ref globalPassRunning, 0);
            }
            Log("Sync pass finished for all enabled accounts.");
        }

        static async Task RunPoolAsync(MailAccountData[] accounts, int maxConcurrency, bool staggeredTorDelay, CancellationToken cancellationToken)
        {
            if (accounts.Length == 0) return;
            using var pool = new SemaphoreSlim(Math.Max(1, maxConcurrency));
            Task[] tasks = [.. accounts.Select(async account =>
            {
                try { await pool.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { return; }

                try
                {
                    await SyncSingleAccountIsolatedAsync(account, staggeredTorDelay, cancellationToken);
                }
                finally
                {
                    pool.Release();
                }
            })];
            await Task.WhenAll(tasks);
        }

        static async Task SyncSingleAccountIsolatedAsync(MailAccountData account, bool staggeredTorDelay, CancellationToken cancellationToken)
        {
            var gate = accountGates.GetOrAdd(account.Id, static _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0, cancellationToken))
                return;

            try
            {
                if (staggeredTorDelay && account.TorOnly)
                    await Task.Delay(RandomNumberGenerator.GetInt32(800, 3201), cancellationToken);

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
                // Shutdown or explicit cancellation.
            }
            catch (Exception ex)
            {
                SyncHealthService.MarkFailure(account.Id, ex.Message);
                Log($"Sync failed for '{account.EmailAddress}': {ex}", LogLevel.Error);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
