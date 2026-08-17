using Polly;
using Polly.Retry;

namespace MyLovelyMail.MainProject.Services
{
    /// <summary>
    /// Central Polly pipelines. Every network call in the app runs through <see cref="RunNetwork"/>
    /// so retry/backoff/timeout behavior is defined exactly once. The inner action is additionally
    /// wrapped in <c>WaitAsync(ct)</c>: even a callee that ignores its CancellationToken cannot
    /// hold an attempt past the timeout.
    /// </summary>
    public static class ResiliencePolicy
    {
        const int MaxRetryAttempts = 3;
        static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
        static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);
        static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(120);

        /// <summary>Outer total timeout → retries with exponential backoff → per-attempt timeout.</summary>
        public static readonly ResiliencePipeline Network = new ResiliencePipelineBuilder()
            .AddTimeout(TotalTimeout)
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = FirstRetryDelay,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(static ex => ex is not OperationCanceledException),
                OnRetry = static args =>
                {
                    Log($"Network retry {args.AttemptNumber + 1}/{MaxRetryAttempts} after: {args.Outcome.Exception?.Message}", LogLevel.Warning);
                    return default;
                }
            })
            .AddTimeout(AttemptTimeout)
            .Build();

        public static async Task<T> RunNetwork<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default) =>
            await Network.ExecuteAsync(async ct => await action(ct).WaitAsync(ct), cancellationToken);

        public static async Task RunNetwork(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
            await Network.ExecuteAsync(async ct => await action(ct).WaitAsync(ct), cancellationToken);
    }
}
