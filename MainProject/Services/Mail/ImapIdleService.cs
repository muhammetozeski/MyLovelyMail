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

        /// <summary>Starts/stops loops so the running set matches which accounts currently want IDLE. Cheap to call often.</summary>
        public static void Refresh()
        {
            var desired = AccountStore.Accounts
                .Where(a => a.Enabled && a.Protocol == IncomingProtocol.Imap && AccountStore.GetSettings(a.Id).UseImapIdle.Value)
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

        public static void StopAll()
        {
            lock (gate)
            {
                foreach (var cts in running.Values) cts.Cancel();
                running.Clear();
            }
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
                        // Server cannot push at all; nothing more this connection can do for "live" mail.
                        Log($"IMAP IDLE not supported by '{account.IncomingHost}' — account stays on periodic sync.", LogLevel.Warning);
                        await client.DisconnectAsync(true, cancellationToken);
                        lock (gate) running.Remove(account.Id);
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
                catch (Exception ex)
                {
                    Log($"IMAP IDLE for '{account.EmailAddress}' dropped: {ex.Message}. Reconnecting in {backoff.TotalSeconds:0}s.", LogLevel.Warning);
                    try { await Task.Delay(backoff, cancellationToken); }
                    catch (OperationCanceledException) { return; }
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, ReconnectDelayMax.TotalSeconds));
                }
            }
        }
    }
}
