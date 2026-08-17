using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services;
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

        public static event Action? OnChanged;

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
                await ComposeService.SendAsync(draft);
                SoundService.Play("success");
            }
            catch (Exception ex)
            {
                Log($"Queued send failed: {ex.Message}", LogLevel.Error);
                NotificationService.Presenter?.Invoke(new MailToast
                {
                    Title = "Send failed",
                    Body = $"'{draft.Subject}' could not be sent — it is still in Drafts. {ex.Message}",
                    SoundName = "default"
                });
            }
        }
    }
}
