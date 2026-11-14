using System.Collections.Concurrent;

namespace Atlas.Server.Platform.Jobs;

/// <summary>The infrastructure loops that stay outside the jobs (§1 class I, §5 „Systémové služby“): each calls <see cref="Beat"/> or
/// <see cref="Failed"/> once a pass. Process state only, nothing is stored: a restart begins it again.</summary>
public sealed class SystemServiceHealth(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, SystemServiceBeat> m_beats = new(StringComparer.Ordinal);

    public void Beat(string serviceId) => m_beats[serviceId] = new SystemServiceBeat(time.GetUtcNow().UtcDateTime, null);

    public void Failed(string serviceId, Exception exception)
        => m_beats[serviceId] = new SystemServiceBeat(time.GetUtcNow().UtcDateTime, exception.Message.Split('\n')[0]);

    public SystemServiceBeat? Last(string serviceId) => m_beats.TryGetValue(serviceId, out var beat) ? beat : null;
}

/// <param name="Error">The first line of the failure, null for a good pass.</param>
public sealed record SystemServiceBeat(DateTime At, string? Error);
