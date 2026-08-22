using System.Collections.Concurrent;
using System.Diagnostics;

namespace MyLovelyMail.MainProject.Services
{
    /// <summary>One measured step: how long it took and when it ran.</summary>
    public sealed record PerfSpan(string Name, double Milliseconds, DateTime WhenUtc);

    /// <summary>
    /// Named stopwatch spans for the steps a user can feel. "Opening this message pins a core for
    /// five seconds" cannot be answered by reading the code — every candidate looks cheap until one
    /// of them is measured — so the open path reports each of its steps here and the debug API
    /// reads them back.
    /// <para>
    /// Kept deliberately tiny: a Stopwatch and a bounded queue. Nothing here allocates per render,
    /// and a span is only recorded where a step is explicitly wrapped.
    /// </para>
    /// </summary>
    public static class PerfTrace
    {
        /// <summary>Spans kept before the oldest are dropped.</summary>
        const int MaxSpans = 300;

        static readonly ConcurrentQueue<PerfSpan> spans = new();

        /// <summary>Wrap a step in <c>using (PerfTrace.Measure("name"))</c>.</summary>
        public static Scope Measure(string name) => new(name);

        public static IReadOnlyList<PerfSpan> Recent => [.. spans];

        public static void Clear()
        {
            while (spans.TryDequeue(out _)) { }
        }

        static void Record(string name, double milliseconds)
        {
            spans.Enqueue(new PerfSpan(name, milliseconds, DateTime.UtcNow));
            while (spans.Count > MaxSpans && spans.TryDequeue(out _)) { }
        }

        /// <summary>A struct so wrapping a step costs no allocation.</summary>
        public readonly struct Scope(string name) : IDisposable
        {
            readonly long startedAt = Stopwatch.GetTimestamp();

            public void Dispose() => Record(name, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }
}
