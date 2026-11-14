namespace Atlas.Server.Features.Auth.Builtin;

/// <summary>
/// The password throttle of sign-in and re-authentication, in fixed one-minute windows independent of the shared office/NAT IP budget.
/// An anonymous sign-in spends the budget of its account and client address (<see cref="FailureLimit"/>) and the account-wide ceiling
/// (<see cref="AccountFailureLimit"/>), so a stranger guessing from one address cannot lock the account out elsewhere (SEC-A-3). A password
/// proof inside a session (re-authentication, password change, identity link) has a budget of its own per account that anonymous guesses
/// never spend. Only failures count (owner decision 28. 9.): a correct password never spends the budget, so an administrator confirming
/// a series of security changes is not throttled. An attempt reserves its place while it runs, so concurrent guesses cannot overshoot
/// the limit; <see cref="Attempt.Fail"/> keeps the place as a failure, disposing without it gives the place back.
/// </summary>
public sealed class BuiltinLoginThrottle
{
    public const int FailureLimit = 10;
    public const int AccountFailureLimit = 100;
    public const string RefusalCode = "password_throttled";
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int PruneAbove = 1024;

    private readonly TimeProvider m_clock;
    private readonly Dictionary<string, Bucket> m_buckets = new(StringComparer.Ordinal);
    private readonly Lock m_lock = new();

    public BuiltinLoginThrottle() : this(TimeProvider.System)
    {
    }

    public BuiltinLoginThrottle(TimeProvider clock)
    {
        m_clock = clock;
    }

    /// <summary>Starts an anonymous sign-in for the account (the username, trimmed and case-insensitive) from the client address; null
    /// with the time until the window ends when either budget is spent.</summary>
    public Attempt? TryBegin(string username, string client, out TimeSpan retryAfter)
    {
        var account = Normalize(username);
        return TryBegin([("sign-in\n" + account + "\n" + client, FailureLimit), ("sign-in\n" + account, AccountFailureLimit)], out retryAfter);
    }

    /// <summary>Starts a password proof of the signed-in account; null with the time until the window ends when its budget is spent.</summary>
    public Attempt? TryBegin(string username, out TimeSpan retryAfter) =>
        TryBegin([("session\n" + Normalize(username), FailureLimit)], out retryAfter);

    /// <summary>Handler-side password proofs use the same reservation and carry the delay to their HTTP endpoint.</summary>
    public Attempt Begin(string username) => TryBegin(username, out var retryAfter)
        ?? throw new ApiException(429, "Příliš mnoho pokusů o ověření hesla. Zkuste to za chvíli.", RefusalCode,
            new Dictionary<string, object?> { ["retryAfter"] = RetryAfterHeader(retryAfter) });

    /// <summary>The Retry-After header value: whole seconds, at least one.</summary>
    public static string RetryAfterHeader(TimeSpan retryAfter) =>
        Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Normalize(string username) => username.Trim().ToLowerInvariant();

    private Attempt? TryBegin((string Key, int Limit)[] budgets, out TimeSpan retryAfter)
    {
        lock (m_lock)
        {
            var now = m_clock.GetUtcNow();
            if (m_buckets.Count > PruneAbove)
            {
                foreach (var stale in m_buckets.Where(pair => pair.Value.Running == 0 && now >= pair.Value.Start + Window).Select(pair => pair.Key).ToArray())
                {
                    m_buckets.Remove(stale);
                }
            }
            var buckets = new Bucket[budgets.Length];
            retryAfter = TimeSpan.Zero;
            for (var index = 0; index < budgets.Length; index++)
            {
                if (!m_buckets.TryGetValue(budgets[index].Key, out var bucket))
                {
                    bucket = new Bucket { Start = now };
                    m_buckets[budgets[index].Key] = bucket;
                }
                else if (now >= bucket.Start + Window)
                {
                    bucket.Start = now;
                    bucket.Failures = 0;
                }
                if (bucket.Failures + bucket.Running >= budgets[index].Limit)
                {
                    retryAfter = TimeSpan.FromTicks(Math.Max(retryAfter.Ticks, (bucket.Start + Window - now).Ticks));
                }
                buckets[index] = bucket;
            }
            if (retryAfter > TimeSpan.Zero)
            {
                return null;
            }
            foreach (var bucket in buckets)
            {
                bucket.Running++;
            }
            return new Attempt(this, budgets.Select(budget => budget.Key).ToArray());
        }
    }

    private void End(string[] keys, bool failed)
    {
        lock (m_lock)
        {
            foreach (var key in keys)
            {
                if (!m_buckets.TryGetValue(key, out var bucket))
                {
                    continue;
                }
                bucket.Running--;
                if (failed)
                {
                    bucket.Failures++;
                }
                else if (bucket.Running == 0 && bucket.Failures == 0)
                {
                    m_buckets.Remove(key);
                }
            }
        }
    }

    private sealed class Bucket
    {
        public DateTimeOffset Start;
        public int Failures;
        public int Running;
    }

    /// <summary>One running password proof. Call <see cref="Fail"/> when the password was wrong (or the account unknown); dispose in
    /// every case.</summary>
    public sealed class Attempt : IDisposable
    {
        private readonly BuiltinLoginThrottle m_owner;
        private readonly string[] m_keys;
        private bool m_failed;
        private bool m_ended;

        internal Attempt(BuiltinLoginThrottle owner, string[] keys)
        {
            m_owner = owner;
            m_keys = keys;
        }

        public void Fail() => m_failed = true;

        public void Dispose()
        {
            if (!m_ended)
            {
                m_ended = true;
                m_owner.End(m_keys, m_failed);
            }
        }
    }
}
