using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>A send waiting out its undo window.</summary>
    public sealed class PendingSend
    {
        public required ComposeDraft Draft { get; init; }
        public required DateTime DueUtc { get; init; }
        internal CancellationTokenSource Cancellation { get; } = new();
    }

    /// <summary>A send that permanently failed after its compose pane closed; shown as a persistent bar.</summary>
    public sealed class FailedSend
    {
        public required ComposeDraft Draft { get; init; }
        public required string Reason { get; init; }
        public required DateTime FailedAtUtc { get; init; }
    }

    /// <summary>
    /// Undo-send: queued drafts wait <see cref="AccountSettings.UndoSendSeconds"/> before the real
    /// <see cref="ComposeService.SendAsync"/> runs, and <see cref="Undo"/> hands the untouched
    /// draft back within that window (SendAsync only deletes the draft after a successful submit,
    /// so cancelling loses nothing). Zero seconds sends immediately. A failure after the compose
    /// pane closed surfaces as a toast, since there is no pane left to show the error in.
    /// </summary>
    public static class OutboxService
    {
        /// <summary>The send currently counting down; null when idle. One at a time is enough for a mail client.</summary>
        public static PendingSend? Current { get; private set; }

        /// <summary>The most recent permanent send failure; a toast vanishes, this stays until Retry/Dismiss.</summary>
        public static FailedSend? LastFailure { get; private set; }

        public static event Action? OnChanged;

        /// <summary>Puts the failed draft back on the queue and clears the failure bar.</summary>
        public static void RetryFailed()
        {
            var failure = LastFailure;
            if (failure == null) return;
            LastFailure = null;
            Enqueue(failure.Draft);
        }

        /// <summary>Clears the failure bar; the draft itself stays safe in Drafts.</summary>
        public static void DismissFailed()
        {
            LastFailure = null;
            OnChanged?.Invoke();
        }

        /// <summary>Queues the draft (or sends immediately when the undo window is 0).</summary>
        public static void Enqueue(ComposeDraft draft)
        {
            int undoSeconds = draft.Account == null ? Settings.UndoSendSeconds.Value
                : AccountStore.GetSettings(draft.Account.Id).UndoSendSeconds.Value;

            if (undoSeconds <= 0)
            {
                _ = RunSendAsync(draft);
                return;
            }

            var pending = new PendingSend
            {
                Draft = draft,
                DueUtc = DateTime.UtcNow.AddSeconds(undoSeconds)
            };
            Current = pending;
            OnChanged?.Invoke();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(undoSeconds), pending.Cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    if (Current == pending)
                    {
                        Current = null;
                        OnChanged?.Invoke();
                    }
                }
                await RunSendAsync(pending.Draft);
            });
        }

        /// <summary>Cancels the countdown and returns the draft for re-editing (null when nothing is pending).</summary>
        public static ComposeDraft? Undo()
        {
            var pending = Current;
            if (pending == null) return null;
            pending.Cancellation.Cancel();
            Current = null;
            OnChanged?.Invoke();
            Log($"Undo send: '{pending.Draft.Subject}' pulled back.");
            return pending.Draft;
        }

        static async Task RunSendAsync(ComposeDraft draft)
        {
            try
            {
                var outcome = await ComposeService.SendAsync(draft);
                if (outcome == SendOutcome.QueuedOffline)
                {
                    NotificationService.Presenter?.Invoke(new MailToast
                    {
                        Title = "Queued — you seem offline",
                        Body = $"'{draft.Subject}' is waiting in Outbox and will be sent automatically.",
                        SoundName = "default"
                    });
                    return;
                }
                SoundService.Play("success");
            }
            catch (Exception ex)
            {
                Log($"Queued send failed: {ex.Message}", LogLevel.Error);
                LastFailure = new FailedSend { Draft = draft, Reason = ex.Message, FailedAtUtc = DateTime.UtcNow };
                OnChanged?.Invoke();
                NotificationService.Presenter?.Invoke(new MailToast
                {
                    Title = "Send failed",
                    Body = $"'{draft.Subject}' could not be sent — it is still in Drafts. {ex.Message}",
                    SoundName = "default"
                });
            }
        }

        /// <summary>
        /// Retries everything parked in each account's local Outbox. Runs after a successful sync
        /// pass (being able to sync means we are online again). Failures stay queued for the next
        /// pass; a summary whose MIME file vanished is dropped as unrecoverable.
        /// </summary>
        public static async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            string outboxFullName = MessageStore.LocalFolderPrefix + ComposeService.LocalOutboxFolderName;
            foreach (var account in AccountStore.Accounts.Where(static a => a.Enabled))
            {
                foreach (var queued in MessageStore.GetSummaries(account.Id, outboxFullName))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (MessageStore.TryLoadMimeMessage(account.Id, outboxFullName, queued.Uid) is not { } message)
                        {
                            MessageStore.RemoveMessages(account.Id, outboxFullName, [queued.Uid]);
                            Log($"Outbox flush: dropped '{queued.Subject}' — its MIME file is gone.", LogLevel.Warning);
                            continue;
                        }
                        await SmtpSendService.SendAsync(account, message, cancellationToken);
                        MessageStore.RemoveMessages(account.Id, outboxFullName, [queued.Uid]);
                        await ComposeService.ArchiveToSentAsync(account, message, cancellationToken);
                        Log($"Outbox flush: sent '{queued.Subject}'.");
                    }
                    catch (Exception ex)
                    {
                        Log($"Outbox flush: '{queued.Subject}' still failing, stays queued: {ex.Message}", LogLevel.Warning);
                    }
                }
            }
        }
    }
}
