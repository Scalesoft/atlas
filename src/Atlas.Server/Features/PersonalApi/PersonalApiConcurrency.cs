using System.Security.Claims;
using System.Threading.RateLimiting;

namespace Atlas.Server.Features.PersonalApi;

/// <summary>
/// Endpoint metadata: personal-token requests whose memory or CPU grows with their body, bounded in how many run at once per token
/// and per owning account across all of its tokens (S-M2). /mcp parses up to 3 MB of JSON and runs the Markdown conversions of the
/// draft tools; the REST Markdown writes parse and merge up to 1 000 000 characters. The "personal-api" window (120 per minute per
/// token) bounds how many a token sends, not how many run at once, and an account may hold many tokens. Over either limit: 429 with
/// Retry-After, nothing queued. The groups are separate, so a long REST write does not block the token's MCP reads. /mcp allows 4 per
/// token, above the parallel tool calls of Claude Code. Applied by the global limiter (<see cref="Limiter"/>), which chains with each
/// endpoint's own policy.
/// </summary>
public sealed class PersonalApiConcurrency
{
    /// <summary>The Retry-After of a refusal: a permit returns when a running request ends, typically within a second.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(1);

    public static readonly PersonalApiConcurrency Mcp = new("mcp", perToken: 4, perAccount: 8);
    public static readonly PersonalApiConcurrency MarkdownWrites = new("markdown", perToken: 2, perAccount: 4);

    private PersonalApiConcurrency(string group, int perToken, int perAccount)
    {
        Group = group;
        PerToken = perToken;
        PerAccount = perAccount;
    }

    public string Group { get; }

    public int PerToken { get; }

    public int PerAccount { get; }

    /// <summary>The global limiter: the token's permit, then the account's, on a marked endpoint; no limit elsewhere (cookies, other routes).</summary>
    public static PartitionedRateLimiter<HttpContext> Limiter() => PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(context => Partition(context, PersonalApiAuthentication.TokenClaim, x => x.PerToken)),
        PartitionedRateLimiter.Create<HttpContext, string>(context => Partition(context, ClaimTypes.NameIdentifier, x => x.PerAccount)));

    /// <summary>A concurrency lease carries no retry time of its own; a refused marked request gets <see cref="RetryAfter"/>.</summary>
    public static bool Applies(HttpContext context) => context.GetEndpoint()?.Metadata.GetMetadata<PersonalApiConcurrency>() is not null;

    private static RateLimitPartition<string> Partition(HttpContext context, string claim, Func<PersonalApiConcurrency, int> limit)
    {
        // Only a personal token's request: the token claim marks one, and its account is the actor the scheme set.
        if (context.GetEndpoint()?.Metadata.GetMetadata<PersonalApiConcurrency>() is { } concurrency
            && context.User.FindFirst(PersonalApiAuthentication.TokenClaim) is not null
            && context.User.FindFirst(claim)?.Value is { } key)
        {
            return RateLimitPartition.GetConcurrencyLimiter(concurrency.Group + ":" + claim + ":" + key,
                _ => new ConcurrencyLimiterOptions { PermitLimit = limit(concurrency), QueueLimit = 0 });
        }
        return RateLimitPartition.GetNoLimiter("");
    }
}
