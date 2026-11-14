using System.Threading.RateLimiting;

namespace Atlas.Server.Features.PersonalApi;

/// <summary>
/// Failed bearer credentials per client address in a fixed one-minute window, like <see cref="Auth.Builtin.BuiltinLoginThrottle"/> counts
/// failed passwords per account: only failures spend the budget, so a valid token never does. Once the address reaches
/// <see cref="FailureLimit"/>, every credential from it that is not found answers 429; a valid one is still looked up and passes, so an
/// expired token retried by a colleague's client never locks out the rest of the office behind the same address (SEC-A-4). A guessing
/// flood costs one indexed lookup per request; the secrets have 256 bits, so the budget bounds the guesser, not the odds of a guess.
/// </summary>
public sealed class PersonalApiCredentialThrottle : IDisposable
{
    public const int FailureLimit = 30;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly PartitionedRateLimiter<string> m_failures = PartitionedRateLimiter.Create<string, string>(client =>
        RateLimitPartition.GetFixedWindowLimiter(client, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = FailureLimit,
            Window = Window,
            QueueLimit = 0,
            AutoReplenishment = true
        }));

    /// <summary>Whether the address may still present a credential; false with the time until its window ends otherwise. Spends nothing.</summary>
    public bool IsOpen(string client, out TimeSpan retryAfter)
    {
        // Zero permits only reports whether the window still has any.
        using var lease = m_failures.AttemptAcquire(client, 0);
        retryAfter = lease.IsAcquired ? TimeSpan.Zero : lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : Window;
        return lease.IsAcquired;
    }

    public void RecordFailure(string client)
    {
        using var _ = m_failures.AttemptAcquire(client);
    }

    public void Dispose() => m_failures.Dispose();
}
