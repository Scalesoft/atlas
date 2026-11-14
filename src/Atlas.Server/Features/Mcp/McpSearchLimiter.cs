using System.Threading.RateLimiting;

namespace Atlas.Server.Features.Mcp;

/// <summary>
/// The search budget of atlas_search per token, with the parameters of the REST "search" policy (30 at once, 10 more every 5 s). The
/// HTTP request itself already spends the shared "personal-api" budget; a tool call cannot use an endpoint policy, so this one is
/// taken by hand. A refusal is a tool error 429 rate_limited with retryAfter in seconds.
/// </summary>
public sealed class McpSearchLimiter : IDisposable
{
    public static readonly TokenBucketRateLimiterOptions Defaults = new()
    {
        TokenLimit = 30, TokensPerPeriod = 10, ReplenishmentPeriod = TimeSpan.FromSeconds(5), AutoReplenishment = true, QueueLimit = 0
    };

    private readonly PartitionedRateLimiter<string> m_limiter;

    public McpSearchLimiter() : this(Defaults)
    {
    }

    public McpSearchLimiter(TokenBucketRateLimiterOptions options)
    {
        m_limiter = PartitionedRateLimiter.Create<string, string>(token => RateLimitPartition.GetTokenBucketLimiter(token, _ => options));
    }

    /// <summary>Null when the search may run, otherwise the seconds to wait.</summary>
    public int? TryAcquire(string tokenId)
    {
        using var lease = m_limiter.AttemptAcquire(tokenId);
        if (lease.IsAcquired)
        {
            return null;
        }
        return lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)) : 5;
    }

    public void Dispose() => m_limiter.Dispose();
}
