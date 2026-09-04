using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services.Tor;
using Polly;
using Polly.Retry;

namespace MyLovelyMail.MainProject.Services
{
    /// <summary>
    /// Central Polly pipelines. Every network call in the app runs through <see cref="RunNetwork"/>
    /// so retry/backoff/timeout behavior is defined exactly once. The inner action is additionally
    /// wrapped in <c>WaitAsync(ct)</c>: even a callee that ignores its CancellationToken cannot
    /// hold an attempt past the timeout.
    /// <para>
    /// There are two pipelines, chosen by the account the call belongs to. A route through Tor is
    /// slower and fails more often than a direct one — a circuit gets built, relays go away
    /// mid-stream, and a first bootstrap can take minutes — so the clear-net budget would report a
    /// perfectly healthy Tor mailbox as broken. The Tor pipeline is patient where the other is not.
    /// </para>
    /// </summary>
    public static class ResiliencePolicy
    {
        const int MaxRetryAttempts = 3;
        static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
        static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);
        static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(120);

        const int TorMaxRetryAttempts = 6;
        static readonly TimeSpan TorFirstRetryDelay = TimeSpan.FromSeconds(3);
        static readonly TimeSpan TorAttemptTimeout = TimeSpan.FromSeconds(150);
        static readonly TimeSpan TorTotalTimeout = TimeSpan.FromMinutes(12);

        /// <summary>Guard for ONE protocol round-trip inside an already-open connection (see <see cref="GuardStep"/>).</summary>
        static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

        /// <summary>The same guard for a round-trip that is travelling through three relays instead of one hop.</summary>
        static readonly TimeSpan TorStepTimeout = TimeSpan.FromSeconds(120);

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
        /// Same shape as <see cref="Network"/> with a budget a circuit can live inside, and jitter
        /// so several accounts coming back after the same outage do not retry in lockstep through
        /// the same relay.
        /// </summary>
        static readonly ResiliencePipeline TorNetwork = new ResiliencePipelineBuilder()
            .AddTimeout(TorTotalTimeout)
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = TorMaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TorFirstRetryDelay,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(static ex => IsWorthRetrying(ex)),
                OnRetry = static args =>
                {
                    Log($"Tor retry {args.AttemptNumber + 1}/{TorMaxRetryAttempts} after: {args.Outcome.Exception?.Message}", LogLevel.Warning);
                    return default;
                }
            })
            .AddTimeout(TorAttemptTimeout)
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
            // "No tor executable exists" and "auto-start is off" are answers, not hiccups; six
            // more passes over the same empty PATH only delay the message the user has to read.
            TorUnavailableException { Recoverable: false } => false,
            _ => true
        };

        /// <summary>The budget this account's traffic gets: a Tor-only mailbox is given the patient one.</summary>
        static ResiliencePipeline PipelineFor(MailAccountData? account) =>
            account is { TorOnly: true } ? TorNetwork : Network;

        public static async Task<T> RunNetwork<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default,
            MailAccountData? account = null) =>
            await PipelineFor(account).ExecuteAsync(async ct => await action(ct).WaitAsync(ct), cancellationToken);

        public static async Task RunNetwork(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default,
            MailAccountData? account = null) =>
            await PipelineFor(account).ExecuteAsync(async ct => await action(ct).WaitAsync(ct), cancellationToken);

        /// <summary>
        /// Guards a SINGLE round-trip of a long multi-step session (e.g. one POP3 header fetch out
        /// of hundreds). No retry and no total budget here on purpose: the caller loops over many
        /// steps, persists progress as it goes, and decides itself what one failed step means —
        /// this only guarantees that no single step can hang the whole session forever.
        /// </summary>
        public static Task<T> GuardStep<T>(Task<T> step, CancellationToken cancellationToken, MailAccountData? account = null) =>
            step.WaitAsync(account is { TorOnly: true } ? TorStepTimeout : StepTimeout, cancellationToken);

        /// <inheritdoc cref="GuardStep{T}(Task{T},CancellationToken,MailAccountData)"/>
        public static Task GuardStep(Task step, CancellationToken cancellationToken, MailAccountData? account = null) =>
            step.WaitAsync(account is { TorOnly: true } ? TorStepTimeout : StepTimeout, cancellationToken);
    }
}
