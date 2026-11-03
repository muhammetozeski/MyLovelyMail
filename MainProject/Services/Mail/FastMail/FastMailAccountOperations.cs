using System.Collections.Concurrent;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Mail.FastMail;

/// <summary>Serializes Fast Mail work and quiesces it before an encrypted account deletion.</summary>
public static class FastMailAccountOperations
{
    sealed class State
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public CancellationTokenSource Cancellation = new();
        public bool Deleting;
    }
    static readonly ConcurrentDictionary<string, State> states = new();
    public static bool IsDeleting(string accountId) => states.TryGetValue(accountId, out var state) && state.Deleting;

    public sealed class Operation : IDisposable
    {
        readonly SemaphoreSlim gate;
        readonly CancellationTokenSource cancellation;
        internal Operation(SemaphoreSlim gate, CancellationTokenSource cancellation) { this.gate = gate; this.cancellation = cancellation; }
        public CancellationToken Token => cancellation.Token;
        public void Dispose() { cancellation.Dispose(); gate.Release(); }
    }

    public static async Task<Operation> EnterAsync(MailAccountData account, CancellationToken cancellationToken)
    {
        var state = states.GetOrAdd(account.Id, static _ => new State());
        CancellationTokenSource linked;
        lock (state)
        {
            if (state.Deleting || AccountStore.GetById(account.Id) == null)
                throw new OperationCanceledException("The Fast Mail account is unavailable.", cancellationToken);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Cancellation.Token);
        }
        try
        {
            await state.Gate.WaitAsync(linked.Token);
            return new Operation(state.Gate, linked);
        }
        catch { linked.Dispose(); throw; }
    }

    public sealed class Deletion : IDisposable
    {
        readonly Action<bool> finish;
        bool completed;
        internal Deletion(Action<bool> finish) => this.finish = finish;
        public void Complete() => completed = true;
        public void Dispose() => finish(completed);
    }

    public static async Task<Deletion> BeginDeleteAsync(string accountId, CancellationToken cancellationToken)
    {
        var state = states.GetOrAdd(accountId, static _ => new State());
        lock (state)
        {
            if (state.Deleting) throw new InvalidOperationException("This account is already being deleted.");
            state.Deleting = true;
            state.Cancellation.Cancel();
        }
        void Reset()
        {
            lock (state)
            {
                state.Cancellation.Dispose();
                state.Cancellation = new CancellationTokenSource();
                state.Deleting = false;
            }
        }
        try { await state.Gate.WaitAsync(cancellationToken); }
        catch { Reset(); throw; }
        return new Deletion(completed =>
        {
            if (!completed) Reset();
            state.Gate.Release();
        });
    }
}
