using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Atlas.Server.Tests.Platform;

/// <summary>
/// The processor time the calling thread has used, for a budget on a synchronous, CPU-bound piece of work (a "bounded time" test).
/// Unlike a stopwatch it does not count the time the thread waits for a core, which in the full parallel run (every core busy) made such
/// budgets fail although the work stayed linear (review V6). Linux and macOS read the thread's clock, Windows its times; anything else
/// falls back to the wall clock.
/// </summary>
internal static class ThreadCpuTime
{
    public static TimeSpan Now()
    {
        if (OperatingSystem.IsWindows())
        {
            return GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user)
                ? TimeSpan.FromTicks(kernel + user)
                : throw new InvalidOperationException("GetThreadTimes failed.");
        }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // CLOCK_THREAD_CPUTIME_ID: 3 on Linux, 16 on macOS.
            return clock_gettime(OperatingSystem.IsLinux() ? 3 : 16, out var time) == 0
                ? TimeSpan.FromTicks(time.Seconds * TimeSpan.TicksPerSecond + time.Nanoseconds / 100)
                : throw new InvalidOperationException("clock_gettime failed.");
        }
        return Stopwatch.GetElapsedTime(0);
    }

    /// <summary>How much processor time <paramref name="work"/> took on this thread.</summary>
    public static TimeSpan Of(Action work)
    {
        var start = Now();
        work();
        return Now() - start;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int clock_gettime(int clock, out TimeSpec time);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
}
