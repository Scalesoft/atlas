using System.Collections.Concurrent;
using Atlas.Server.Features.Ai.Settings;

namespace Atlas.Server.Features.Ai.Providers;

/// <summary>
/// The profile's <c>MaxConcurrency</c>, per revision. Indexing (<c>index.embed</c>) holds at most max(1, MaxConcurrency − 1)
/// slots and queues for them, so one slot always stays for interactive calls (search, suggestions, answers, the admin test). An
/// interactive call waits at most its task's <see cref="AiTaskDefinition.SlotWait"/> for a slot (zero for every task before phase
/// 3, ten seconds for an answer, R6) and then fails with <see cref="AiOutcome.Busy"/>; the caller falls back (search stays
/// lexical) or says the answer could not be prepared. A busy rejection is not a failure of the profile. The wait runs on the
/// injected clock, so a fake clock drives it in tests.
/// </summary>
public sealed class AiConcurrencyGate
{
    private readonly ConcurrentDictionary<Guid, Slots> m_slots = new();
    private readonly TimeProvider m_clock;

    public AiConcurrencyGate() : this(TimeProvider.System)
    {
    }

    public AiConcurrencyGate(TimeProvider clock)
    {
        m_clock = clock;
    }

    public static bool IsBackground(string task) => task == AiTaskCatalog.IndexEmbed;

    /// <summary>Interactive calls of the profile revision waiting for a slot right now (diagnostics and tests).</summary>
    public int Waiting(Guid revisionId) => m_slots.TryGetValue(revisionId, out var slots) ? Volatile.Read(ref slots.Waiting) : 0;

    /// <summary>A slot for one call; dispose it when the call has finished (a stream: when it is fully read).</summary>
    /// <exception cref="AiCallException"><see cref="AiOutcome.Busy"/> for an interactive call without a free slot in time.</exception>
    public async ValueTask<IDisposable> EnterAsync(AiProfileSnapshot profile, string task, CancellationToken ct)
    {
        var slots = m_slots.GetOrAdd(profile.RevisionId, _ => new Slots(profile.MaxConcurrency));
        if (!IsBackground(task))
        {
            if (slots.All.Wait(0))
            {
                return new Lease(slots, false);
            }
            var wait = AiTaskCatalog.Find(task)?.SlotWait ?? TimeSpan.Zero;
            if (wait <= TimeSpan.Zero)
            {
                throw new AiCallException(AiOutcome.Busy);
            }
            using var expiry = new CancellationTokenSource(wait, m_clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token);
            Interlocked.Increment(ref slots.Waiting);
            try
            {
                await slots.All.WaitAsync(linked.Token);
                return new Lease(slots, false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AiCallException(AiOutcome.Busy);
            }
            finally
            {
                Interlocked.Decrement(ref slots.Waiting);
            }
        }
        await slots.Background.WaitAsync(ct);
        try
        {
            await slots.All.WaitAsync(ct);
        }
        catch
        {
            slots.Background.Release();
            throw;
        }
        return new Lease(slots, true);
    }

    private sealed class Slots(int maxConcurrency)
    {
        public int Waiting;

        public SemaphoreSlim All { get; } = new(Math.Max(1, maxConcurrency));
        public SemaphoreSlim Background { get; } = new(Math.Max(1, maxConcurrency - 1));
    }

    private sealed class Lease(Slots slots, bool background) : IDisposable
    {
        private int m_released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref m_released, 1) != 0)
            {
                return;
            }
            slots.All.Release();
            if (background)
            {
                slots.Background.Release();
            }
        }
    }
}
