namespace Atlas.Server.Platform.Jobs;

/// <summary>
/// When a heavy periodic job (<see cref="IJobDefinition.RunsWhenQuiet"/>: backups, maintenance) may start (owner, 2026-11-08): only
/// when no page was edited for <see cref="JobQuietOptions.Window"/>. Otherwise <see cref="JobRunner"/> leaves its run queued and
/// schedules it again after <see cref="JobQuietOptions.Postpone"/>, until <see cref="JobQuietOptions.Deadline"/> after it was queued
/// forces it. An edit is a live update (AppendUpdateHandler) or a save (SavePageHandler), noted in memory: the live relay runs in
/// one process (ADR-002), and a start counts as an edit, because the writers reconnect then.
/// </summary>
public sealed class JobQuiet(JobQuietOptions options, TimeProvider clock)
{
    private long m_lastEdit = clock.GetUtcNow().UtcTicks;

    public void Edited() => Interlocked.Exchange(ref m_lastEdit, clock.GetUtcNow().UtcTicks);

    /// <summary>Null: the run starts now; otherwise when to ask again.</summary>
    public DateTime? Postpone(DateTime queuedAt)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var quiet = now - new DateTime(Interlocked.Read(ref m_lastEdit), DateTimeKind.Utc);
        return quiet >= options.Window || now >= DateTime.SpecifyKind(queuedAt, DateTimeKind.Utc) + options.Deadline ? null : now + options.Postpone;
    }
}

/// <summary>The section <c>JobQuiet</c> (<see cref="JobQuiet"/>).</summary>
public sealed class JobQuietOptions
{
    /// <summary>No edit for this long is quiet.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a run waits before it asks again.</summary>
    public TimeSpan Postpone { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>A run queued this long ago starts even while pages are edited.</summary>
    public TimeSpan Deadline { get; set; } = TimeSpan.FromMinutes(30);

    public static JobQuietOptions From(IConfiguration configuration)
    {
        var options = new JobQuietOptions();
        configuration.GetSection("JobQuiet").Bind(options);
        return options;
    }
}
