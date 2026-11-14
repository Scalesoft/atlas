using Atlas.Server.Features.Ai.Availability;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using Wolverine;

namespace Atlas.Server.Features.Ai;

/// <summary>
/// The AI configuration as one immutable snapshot: the master switch, the current revision of each profile kind, the task
/// switches and the active index generation. Read from memory by the hot paths (capabilities, search, the index worker, the
/// probes), never from the database.
/// </summary>
/// <param name="Profiles">The kinds with a current revision; a kind without one is not configured.</param>
/// <param name="Tasks">Every catalog task's effective switch (<see cref="CurrentAiSettings.ResolveTasks"/>).</param>
/// <param name="ActiveGeneration">The generation queries use; null before V2 has built one.</param>
public sealed record AiConfigurationSnapshot(bool Enabled, IReadOnlyDictionary<string, AiProfileSnapshot> Profiles,
    IReadOnlyDictionary<string, AiTaskSwitch> Tasks, AiActiveGeneration? ActiveGeneration)
{
    /// <summary>Nothing stored, or no database yet: AI is off (AI-24).</summary>
    public static AiConfigurationSnapshot Off { get; } = new(false, new Dictionary<string, AiProfileSnapshot>(),
        CurrentAiSettings.ResolveTasks(null), null);

    public AiProfileSnapshot? Profile(string kind) => Profiles.GetValueOrDefault(kind);

    /// <summary>The profile a task calls: its catalog kind, except that quick tasks run on the chat profile when the quick
    /// profile says "Použít profil Chat" (<see cref="AiProfileSnapshot.UseChatProfile"/>; phase 4 uses it, O17). Null for an
    /// unknown task or an unconfigured profile.</summary>
    public AiProfileSnapshot? ProfileFor(string task)
    {
        if (AiTaskCatalog.Find(task) is not { } definition)
        {
            return null;
        }
        var profile = Profile(definition.ProfileKind);
        return definition.ProfileKind == AiProfileKind.Quick && profile is { UseChatProfile: true } ? Profile(AiProfileKind.Chat) : profile;
    }

    public bool TaskEnabled(string task) => Tasks.TryGetValue(task, out var value) && value.Enabled;

    /// <summary>The admin's daily limit per user of a limitable task; null = none.</summary>
    public int? DailyLimit(string task) => Tasks.TryGetValue(task, out var value) ? value.DailyLimit : null;
}

/// <summary>Reads the configuration rows into a snapshot. Production goes through Wolverine (<see cref="LoadAiConfiguration"/>);
/// the database-free tests of availability set a snapshot instead.</summary>
public interface IAiConfigurationLoader
{
    Task<AiConfigurationSnapshot> LoadAsync(CancellationToken ct);
}

/// <summary>One short read transaction in a fresh scope, like the other background readers (no request scope, no actor).</summary>
public sealed class WolverineAiConfigurationLoader(IServiceScopeFactory scopes, AtlasDatabase database) : IAiConfigurationLoader
{
    public async Task<AiConfigurationSnapshot> LoadAsync(CancellationToken ct)
    {
        // Before setup has created the database there is nothing to read, and AI is off.
        if (!database.Initialized)
        {
            return AiConfigurationSnapshot.Off;
        }
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<AiConfigurationSnapshot>(new LoadAiConfiguration(), ct);
    }
}

/// <summary>
/// The in-process AI configuration (R3, O16: Atlas runs as one instance). Loaded lazily, dropped after a committed change
/// (<see cref="IAiConfigurationChanged"/>) and otherwise reloaded once it is <see cref="RefreshInterval"/> old, so capabilities
/// and the search hot path read memory; the probe service's 5 s tick keeps it warm. Every load hands the snapshot to
/// <see cref="AiAvailability"/>, which resets a kind whose revision changed to unverified.
/// </summary>
public sealed class AiConfigurationCache : IAiConfigurationChanged
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private readonly IAiConfigurationLoader m_loader;
    private readonly AiAvailability m_availability;
    private readonly TimeProvider m_clock;
    private readonly ILogger<AiConfigurationCache> m_logger;
    private readonly AiLiveSignals? m_signals;
    private readonly SemaphoreSlim m_load = new(1, 1);
    private readonly SemaphoreSlim m_changed = new(0, 1);
    private Entry? m_entry;
    private int m_generation;

    public AiConfigurationCache(IAiConfigurationLoader loader, AiAvailability availability, TimeProvider clock, ILogger<AiConfigurationCache> logger,
        AiLiveSignals? signals = null)
    {
        m_loader = loader;
        m_availability = availability;
        m_clock = clock;
        m_logger = logger;
        m_signals = signals;
    }

    /// <summary>The current snapshot: from memory while fresh, else one load shared by all concurrent callers. A failed load
    /// keeps the previous snapshot (or <see cref="AiConfigurationSnapshot.Off"/>) and is retried by the next caller.</summary>
    public ValueTask<AiConfigurationSnapshot> GetAsync(CancellationToken ct)
    {
        var entry = Volatile.Read(ref m_entry);
        return IsFresh(entry) ? ValueTask.FromResult(entry!.Snapshot) : new ValueTask<AiConfigurationSnapshot>(LoadAsync(ct));
    }

    /// <summary>After the commit of a change: the next read loads, and the probe service wakes up to verify a new revision.</summary>
    public void Changed()
    {
        Interlocked.Increment(ref m_generation);
        // The settings may have switched a task or the whole AI: everyone reads the capabilities again.
        m_signals?.CapabilitiesChanged();
        if (m_changed.CurrentCount == 0)
        {
            try
            {
                m_changed.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another change released it first; one wake-up is enough.
            }
        }
    }

    /// <summary>Waits for <see cref="Changed"/> or the timeout, whichever comes first (the probe service's tick).</summary>
    public Task<bool> WaitForChangeAsync(TimeSpan timeout, CancellationToken ct) => m_changed.WaitAsync(timeout, ct);

    private bool IsFresh(Entry? entry)
        => entry is not null && entry.Generation == Volatile.Read(ref m_generation) && m_clock.GetUtcNow() - entry.LoadedAt < RefreshInterval;

    private async Task<AiConfigurationSnapshot> LoadAsync(CancellationToken ct)
    {
        await m_load.WaitAsync(ct);
        try
        {
            var entry = m_entry;
            if (IsFresh(entry))
            {
                return entry!.Snapshot;
            }
            // Taken before the read: a change committed during it leaves the result stale, so the next caller reloads.
            var generation = Volatile.Read(ref m_generation);
            var loadedAt = m_clock.GetUtcNow();
            AiConfigurationSnapshot snapshot;
            try
            {
                snapshot = await m_loader.LoadAsync(ct);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                m_logger.LogWarning("Loading the AI configuration failed ({ErrorType}); AI keeps its last known configuration", exception.GetType().Name);
                return entry?.Snapshot ?? AiConfigurationSnapshot.Off;
            }
            Volatile.Write(ref m_entry, new Entry(snapshot, generation, loadedAt));
            m_availability.Sync(snapshot);
            return snapshot;
        }
        finally
        {
            m_load.Release();
        }
    }

    private sealed record Entry(AiConfigurationSnapshot Snapshot, int Generation, DateTimeOffset LoadedAt);
}
