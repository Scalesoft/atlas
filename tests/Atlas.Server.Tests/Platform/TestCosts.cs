using System.Collections.Concurrent;
using System.Diagnostics;

namespace Atlas.Server.Tests.Platform;

/// <summary>
/// What the shared fixtures cost one test process: how many databases and hosts it created and the time spent in each step
/// (summed over parallel tests, so it exceeds the wall time). With <c>ATLAS_TEST_COSTS</c> set to a file name, the process keeps
/// its line in that file with its process id appended (VSTest ends the test host before an exit handler would write it);
/// tooling/ci/server-tests.sh prints the files, so a cost that creeps back shows in the CI log.
/// </summary>
internal static class TestCosts
{
    private static readonly ConcurrentDictionary<string, (long Count, long Ticks)> s_steps = new();
    private static readonly Stopwatch s_process = Stopwatch.StartNew();

    private static readonly Timer? s_writer = Environment.GetEnvironmentVariable("ATLAS_TEST_COSTS") is { Length: > 0 } file
        ? new Timer(_ => Write(file + "." + Environment.ProcessId), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2))
        : null;

    private static void Write(string file)
    {
        try
        {
            File.WriteAllText(file, Summary() + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Someone reads the file, or the last write still runs: the next tick writes it. An exception here would end the test process.
        }
    }

    public static string Summary() => $"pid {Environment.ProcessId} wall {s_process.Elapsed.TotalSeconds:F0}s, peak memory {Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB: " + string.Join(", ", s_steps.OrderBy(step => step.Key)
        .Select(step => $"{step.Key} {step.Value.Count}x {TimeSpan.FromTicks(step.Value.Ticks).TotalSeconds:F1}s"))
        + "; private hosts by prefix: " + string.Join(", ", s_private.OrderByDescending(use => use.Value).Take(40).Select(use => $"{use.Key} {use.Value}"));

    private static readonly ConcurrentDictionary<string, int> s_private = new();

    /// <summary>A host or site of a test's own was built for this schema prefix: the line names the prefixes that build the most.</summary>
    public static void Private(string prefix) => s_private.AddOrUpdate(prefix, 1, (_, count) => count + 1);

    public static long Count(string step) => s_steps.TryGetValue(step, out var cost) ? cost.Count : 0;

    public static void Add(string step, long startedAt)
    {
        GC.KeepAlive(s_writer);
        var ticks = Stopwatch.GetElapsedTime(startedAt).Ticks;
        s_steps.AddOrUpdate(step, (1, ticks), (_, cost) => (cost.Count + 1, cost.Ticks + ticks));
    }

    public static async Task<T> MeasureAsync<T>(string step, Func<Task<T>> act)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            return await act();
        }
        finally
        {
            Add(step, startedAt);
        }
    }

    public static async Task MeasureAsync(string step, Func<Task> act)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            await act();
        }
        finally
        {
            Add(step, startedAt);
        }
    }
}
