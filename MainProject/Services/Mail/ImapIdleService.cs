using MailKit;
using MailKit.Net.Imap;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Keeps one persistent IMAP IDLE connection per enabled account that opts in via
    /// <see cref="AccountSettings.UseImapIdle"/>, so new mail lands in <see cref="MessageStore"/>
    /// (and the UI, through <see cref="MessageStore.OnFolderChanged"/>) within seconds instead
    /// of waiting for the next periodic <see cref="SyncScheduler"/> pass. POP3 has no push mechanism in
    /// the protocol itself, so POP3 accounts are never eligible here — they stay on periodic polling.
    /// </summary>
    public static class ImapIdleService
    {
        /// <summary>Re-issue IDLE before the ~29-minute RFC 2177 server timeout; also caps how long a dropped CountChanged could go unnoticed.</summary>
        static readonly TimeSpan IdleReissueInterval = TimeSpan.FromMinutes(9);
        static readonly TimeSpan ReconnectDelayMin = TimeSpan.FromSeconds(2);
        static readonly TimeSpan ReconnectDelayMax = TimeSpan.FromMinutes(2);

        static readonly Dictionary<string, CancellationTokenSource> running = [];
        static readonly Lock gate = new();

        /// <summary>Hosts that answered "no IDLE capability" — retrying those every sync pass would just burn a connection each time.</summary>
        static readonly HashSet<string> idleUnsupportedAccountIds = [];

        /// <summary>Starts/stops loops so the running set matches which accounts currently want IDLE. Cheap to call often.</summary>
        public static void Refresh()
        {
            var desired = AccountStore.Accounts
                .Where(a => a.Enabled && a.Protocol == IncomingProtocol.Imap
                    && AccountStore.GetSettings(a.Id).UseImapIdle.Value
                    && !idleUnsupportedAccountIds.Contains(a.Id))
                .ToDictionary(a => a.Id);

            lock (gate)
            {
                foreach (string accountId in running.Keys.Where(id => !desired.ContainsKey(id)).ToList())
                {
                    running[accountId].Cancel();
                    running.Remove(accountId);
                }

                foreach (var account in desired.Values)
                {
                    if (running.ContainsKey(account.Id)) continue;
                    var cts = new CancellationTokenSource();
                    running[account.Id] = cts;
                    _ = RunIdleLoopAsync(account, cts.Token);
                }
            }
        }

        /// <summary>Account ids whose IDLE loop is alive right now. Read by the debug API; the loop set is otherwise invisible.</summary>
        public static IReadOnlyList<string> RunningAccountIds
        {
            get { lock (gate) return [.. running.Keys]; }
        }

        /// <summary>
        /// Ends one account's loop so <see cref="Refresh"/> builds a new one.
        /// <para>
        /// A running loop holds a connection opened under the settings of the moment it started,
        /// and <see cref="Refresh"/> never touches a loop that is already running. So turning an
        /// account Tor-only left its IDLE socket talking to the provider on the DIRECT route,
        /// re-issuing IDLE every nine minutes until new mail happened to arrive — which can be all
        /// night, with the settings page reporting a Tor route in use the whole time.
        /// </para>
        /// </summary>
        public static void Drop(string accountId)
        {
            CancellationTokenSource? cts;
            lock (gate)
                if (!running.Remove(accountId, out cts)) return;

            try { cts.Cancel(); } catch (ObjectDisposedException) { /* Already finished on its own. */ }
            Log($"IMAP IDLE loop dropped for account {accountId}; a new one opens on the current settings.");
            Refresh();
        }

        static async Task RunIdleLoopAsync(MailAccountData account, CancellationToken cancellationToken)
        {
            TimeSpan backoff = ReconnectDelayMin;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var client = await MailConnections.OpenImapAsync(account, cancellationToken);
                    var inbox = client.Inbox;
                    await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

                    if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
                    {
                        // Server cannot push at all. Remember that, or the next Refresh() (every
                        // sync pass calls it) would reconnect and rediscover the same answer forever.
                        Log($"IMAP IDLE not supported by '{account.IncomingHost}' — account stays on periodic sync.", LogLevel.Warning);
                        await client.DisconnectAsync(true, cancellationToken);
                        lock (gate)
                        {
                            idleUnsupportedAccountIds.Add(account.Id);
                            running.Remove(account.Id);
                        }
                        return;
                    }

                    backoff = ReconnectDelayMin;
                    bool arrived = false;
                    CancellationTokenSource? activeIdleDone = null;
                    void OnCountChanged(object? sender, EventArgs e)
                    {
                        arrived = true;
                        // IdleAsync only returns when its done-token fires — without this cancel
                        // the "instant" push would sit inside IDLE until the reissue interval.
                        try { activeIdleDone?.Cancel(); } catch (ObjectDisposedException) { }
                    }
                    inbox.CountChanged += OnCountChanged;
                    Log($"IMAP IDLE active: {account.EmailAddress}");

                    try
                    {
                        while (!cancellationToken.IsCancellationRequested && !arrived)
                        {
                            using var doneSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            activeIdleDone = doneSource;
                            doneSource.CancelAfter(IdleReissueInterval);
                            await client.IdleAsync(doneSource.Token, cancellationToken);
                        }
                    }
                    finally
                    {
                        activeIdleDone = null;
                        inbox.CountChanged -= OnCountChanged;
                    }

                    await client.DisconnectAsync(true, cancellationToken);

                    if (arrived && !cancellationToken.IsCancellationRequested)
                    {
                        Log($"IMAP IDLE new-mail signal: {account.EmailAddress} — syncing now.");
                        await ImapSyncService.SyncAccountAsync(account, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                // A refused password will be refused again, so reconnecting only replays the same
                // failed login for as long as the app runs — the backoff caps the RATE, never the
                // total, which is how an account gets rate-limited by its own provider. The
                // scheduled pass still reports the account as failing; only the loop gives up.
                catch (Exception ex) when (ex is MailKit.Security.AuthenticationException or MailKit.ServiceNotAuthenticatedException)
                {
                    SyncHealthService.MarkFailure(account.Id, ex.Message);
                    Log($"IMAP IDLE for '{account.EmailAddress}' stopped: {ex.Message}. Reconnecting cannot fix a rejected password.", LogLevel.Error);
                    return;
                }
                // No Tor on the machine, or auto-starting one is switched off. Reconnecting every
                // two minutes cannot fix either, so the loop lets go of its slot; the next
                // Refresh() — one per sync pass — starts it again once the machine can offer a route.
                catch (Tor.TorUnavailableException ex) when (!ex.Recoverable)
                {
                    SyncHealthService.MarkFailure(account.Id, ex.Message);
                    Log($"IMAP IDLE for '{account.EmailAddress}' stopped: {ex.Message}", LogLevel.Error);
                    lock (gate) running.Remove(account.Id);
                    return;
                }
                catch (Exception ex)
                {
                    SyncHealthService.MarkFailure(account.Id, ex.Message);

                    // A long-held IDLE that dropped usually dropped because its circuit did. Coming
                    // back on the same one repeats the failure; the next connection picks a new path.
                    if (account.TorOnly) Tor.TorService.RotateCircuit(account.Id);

                    Log($"IMAP IDLE for '{account.EmailAddress}' dropped: {ex.Message}. Reconnecting in {backoff.TotalSeconds:0}s.", LogLevel.Warning);
                    try { await Task.Delay(backoff, cancellationToken); }
                    catch (OperationCanceledException) { return; }
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, ReconnectDelayMax.TotalSeconds));
                }
            }
        }
    }
}
