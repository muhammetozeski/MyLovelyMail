using System.Threading.Channels;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Storage;

namespace MyLovelyMail.MainProject.Services.Mail
{
    /// <summary>
    /// Producer/consumer pool between fetchers and the store: any number of fetchers Add
    /// summaries as they arrive, one drain loop upserts whatever has accumulated since its last
    /// write. Coalesces rapid bursts over a brief window so large mailbox syncs do not rewrite
    /// index.jsonl and repaint the UI hundreds of times per second.
    /// </summary>
    public sealed class SummaryPump(string accountId, string folderFullName)
    {
        const int BurstCoalesceMs = 90;
        readonly Channel<MailMessageSummary> pool = Channel.CreateUnbounded<MailMessageSummary>();

        public void Add(MailMessageSummary summary) => pool.Writer.TryWrite(summary);

        /// <summary>Call once after every producer finished; the drain loop then ends.</summary>
        public void Complete() => pool.Writer.Complete();

        /// <summary>Runs until <see cref="Complete"/>; returns everything that passed through, in arrival order.</summary>
        public async Task<List<MailMessageSummary>> DrainToStoreAsync(CancellationToken cancellationToken = default)
        {
            List<MailMessageSummary> drained = [];
            while (await pool.Reader.WaitToReadAsync(cancellationToken))
            {
                if (!pool.Reader.Completion.IsCompleted)
                {
                    try { await Task.Delay(BurstCoalesceMs, cancellationToken); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                }

                List<MailMessageSummary> burst = [];
                while (pool.Reader.TryRead(out var summary))
                    burst.Add(summary);
                if (burst.Count == 0) continue;
                drained.AddRange(burst);
                MessageStore.UpsertSummaries(accountId, folderFullName, burst);
            }
            return drained;
        }
    }
}
