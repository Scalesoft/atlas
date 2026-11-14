using System.Threading.RateLimiting;

namespace Atlas.Server.Features.Vaults.Release;

/// <summary>A fixed window of release attempts per account (<see cref="SecretReleaseOptions.PerMinute"/>), as BuiltinLoginThrottle.</summary>
public sealed class SecretReleaseThrottle : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> m_limiter;

    public SecretReleaseThrottle(SecretReleaseOptions options)
    {
        m_limiter = PartitionedRateLimiter.Create<Guid, Guid>(userId => RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.PerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    }

    public bool TryAcquire(Guid userId)
    {
        using var lease = m_limiter.AttemptAcquire(userId);
        return lease.IsAcquired;
    }

    public void Dispose() => m_limiter.Dispose();
}
