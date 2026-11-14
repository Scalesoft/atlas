namespace Atlas.Server.Features.Boards;

/// <summary>
/// The live messages of each board's committed batches (BoardChanged, the board object's ObjectChanged and its view sessions'
/// membership), sent outside the request that wrote them, one board's in the order their commits handed them over (COL2-3): the
/// answer to a batch never waits for SignalR to reach every participant, a slow browser among 30 held every writer's confirmation for
/// seconds. Each message still carries its sequence, so a client that misses one or hears it late reads the gap back.
/// </summary>
/// <remarks>In memory, like the live subscriptions: one active host. What waits when the host stops is lost; clients that come back
/// read the board again.</remarks>
public sealed class BoardBroadcasts
{
    private readonly ILogger<BoardBroadcasts> m_log;
    private readonly Lock m_lock = new();
    private readonly Dictionary<Guid, Task> m_tails = [];

    public BoardBroadcasts(ILogger<BoardBroadcasts> log)
    {
        m_log = log;
    }

    /// <summary>Queues <paramref name="send"/> behind what the board already queued and returns at once. Best effort: a failure
    /// only logs, and never stops what is queued behind it.</summary>
    public void Enqueue(Guid boardId, Func<Task> send)
    {
        lock (m_lock)
        {
            var tail = RunAfterAsync(m_tails.GetValueOrDefault(boardId) ?? Task.CompletedTask, send);
            m_tails[boardId] = tail;
            _ = tail.ContinueWith(done =>
            {
                lock (m_lock)
                {
                    if (m_tails.TryGetValue(boardId, out var last) && last == done)
                    {
                        m_tails.Remove(boardId);
                    }
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Completes once everything queued so far has been sent (tests, an orderly stop).</summary>
    public Task IdleAsync()
    {
        lock (m_lock)
        {
            return Task.WhenAll(m_tails.Values);
        }
    }

    private async Task RunAfterAsync(Task previous, Func<Task> send)
    {
        // Always off the caller's thread (and out of the lock): the request answers without running any of it.
        await previous.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.SuppressThrowing);
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            m_log.LogWarning(ex, "A live message of a board batch could not be sent");
        }
    }
}
