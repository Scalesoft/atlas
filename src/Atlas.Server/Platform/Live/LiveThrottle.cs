namespace Atlas.Server.Platform.Live;

/// <summary>
/// Coalesces the signals of /hubs/live per key (a job run, the server log): the first goes out at once, those posted within
/// <see cref="Interval"/> after a send go out once at its end, the latest posted winning, so a burst ends in one message and the
/// final state is never lost. A signal only says "read again"; nothing is queried here.
/// </summary>
public sealed class LiveThrottle(TimeProvider clock, ILogger<LiveThrottle> log)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly Lock m_lock = new();
    private readonly Dictionary<string, Func<Task>> m_waiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> m_sent = new(StringComparer.Ordinal);

    public void Post(string key, Func<Task> send)
    {
        TimeSpan wait;
        lock (m_lock)
        {
            var scheduled = m_waiting.ContainsKey(key);
            m_waiting[key] = send;
            if (scheduled)
            {
                return;
            }
            wait = m_sent.TryGetValue(key, out var at) ? Interval - clock.GetElapsedTime(at) : TimeSpan.Zero;
        }
        _ = SendAsync(key, wait);
    }

    private async Task SendAsync(string key, TimeSpan wait)
    {
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, clock);
        }
        Func<Task> send;
        lock (m_lock)
        {
            send = m_waiting[key];
            m_waiting.Remove(key);
            m_sent[key] = clock.GetTimestamp();
            // Keys quiet for longer than the interval (ended runs) are forgotten: their next signal goes out at once anyway.
            if (m_sent.Count > 256)
            {
                foreach (var quiet in m_sent.Where(item => clock.GetElapsedTime(item.Value) >= Interval).Select(item => item.Key).ToList())
                {
                    m_sent.Remove(quiet);
                }
            }
        }
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            // Debug: a failing send must not feed the server log's own signal.
            log.LogDebug(ex, "A live signal for {Key} could not be sent", key);
        }
    }
}
