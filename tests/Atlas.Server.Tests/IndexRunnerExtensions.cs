using Atlas.Server.Features.AiIndex;

namespace Atlas.Server.Tests;

internal static class IndexRunnerExtensions
{
    /// <summary>Rounds at one instant until nothing is due, as a test drives the index with a fake or fixed clock. The job
    /// (IndexJob) reads the clock again for every round, so a long drain never leases against a stale time.</summary>
    public static async Task<int> RunUntilIdleAsync(this IndexRunner runner, DateTimeOffset now, CancellationToken ct)
    {
        var total = 0;
        int finished;
        while ((finished = await runner.RunOnceAsync(now, ct)) > 0)
        {
            total += finished;
        }
        return total;
    }
}
