using Microsoft.Extensions.Caching.Memory;

namespace Atlas.Server.Features.YouTrack;

// Cache contains only successful read snapshots (a resolved miss is one: null). Every lookup still checks live wiki ACL
// and configuration revision; failures never fall back to another scope's stale entries.
public sealed class YouTrackReadCache : IDisposable
{
    private readonly MemoryCache m_entries = new(new MemoryCacheOptions { SizeLimit = 256 });

    public bool TryGet<T>(string key, out T? value) => m_entries.TryGetValue(key, out value);

    public void Set<T>(string key, T value) => m_entries.Set(key, value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) });

    public void Dispose() => m_entries.Dispose();
}
