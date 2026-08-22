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

        /// <summary>Guard for ONE protocol round-trip inside an already-open connection (see <see cref="GuardStep"/>).</summary>
        static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Outer total timeout → retries with exponential backoff → per-attempt timeout.</summary>
        static readonly ResiliencePipeline Network = new ResiliencePipelineBuilder()
            .AddTimeout(TotalTimeout)
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = FirstRetryDelay,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(static ex => IsWorthRetrying(ex)),
                OnRetry = static args =>
                {
                    Log($"Network retry {args.AttemptNumber + 1}/{MaxRetryAttempts} after: {args.Outcome.Exception?.Message}", LogLevel.Warning);
                    return default;
                }
            })
            .AddTimeout(AttemptTimeout)
            .Build();

        /// <summary>
        /// Whether trying again could plausibly help. A refused password will be refused three
        /// more times, so retrying it only turns one mistyped password into four failed logins —
        /// against Gmail or Yahoo, on every scheduled pass, which is how a provider starts
        /// rate-limiting the account. Sockets, TLS and timeouts stay retryable: those do heal.
        /// </summary>
        static bool IsWorthRetrying(Exception exception) => exception switch
        {
            OperationCanceledException => false,
            MailKit.Security.AuthenticationException => false,
            MailKit.ServiceNotAuthenticatedException => false,
            _ => true
        };

        public static async Task<T> RunNetwork<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default) =>
            await Network.ExecuteAsync(async ct => await action(ct).WaitAsync(ct), cancellationToken);

        public static async Task RunNetwork(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
            await Network.ExecuteAsync(async ct => await action(ct).WaitAsync(ct), cancellationToken);

        /// <summary>
        /// Guards a SINGLE round-trip of a long multi-step session (e.g. one POP3 header fetch out
        /// of hundreds). No retry and no total budget here on purpose: the caller loops over many
        /// steps, persists progress as it goes, and decides itself what one failed step means —
        /// this only guarantees that no single step can hang the whole session forever.
        /// </summary>
        public static Task<T> GuardStep<T>(Task<T> step, CancellationToken cancellationToken) =>
            step.WaitAsync(StepTimeout, cancellationToken);

        /// <inheritdoc cref="GuardStep{T}(Task{T},CancellationToken)"/>
        public static Task GuardStep(Task step, CancellationToken cancellationToken) =>
            step.WaitAsync(StepTimeout, cancellationToken);
    }
}
