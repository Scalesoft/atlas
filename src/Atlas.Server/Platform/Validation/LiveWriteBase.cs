namespace Atlas.Server.Platform.Validation;

/// <summary>
/// The version an agent read, accepted for one live write (3.6-H): while people type, autosave moves the page version on, so an agent's
/// <c>expectedVersion</c> names the revision it read, not the current one. Inside <see cref="Use"/> (the live write of that page, under
/// its document gate) <see cref="Rules.ExpectVersion"/> takes that read version for the current one; the write's target is then checked
/// against the read revision instead (CollaborationCheckpoints, LiveWriteTarget). Only server code sets it; nothing a client sends can.
/// </summary>
public static class LiveWriteBase
{
    private static readonly AsyncLocal<(int Read, int Current)?> s_base = new();

    public static bool Accepts(int current, int expected) => s_base.Value is { } live && live.Read == expected && live.Current == current;

    public static IDisposable Use(int read, int current)
    {
        var previous = s_base.Value;
        s_base.Value = (read, current);
        return new Restore(previous);
    }

    private sealed class Restore((int, int)? previous) : IDisposable
    {
        public void Dispose() => s_base.Value = previous;
    }
}
