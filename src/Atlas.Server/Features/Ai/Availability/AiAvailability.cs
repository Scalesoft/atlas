using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Domain;

namespace Atlas.Server.Features.Ai.Availability;

/// <summary>The state of one profile kind in Administrace → AI → Přehled; only <see cref="Available"/> serves capabilities.</summary>
public enum AiProfileState
{
    /// <summary>No revision is assigned to the kind.</summary>
    NotConfigured,
    /// <summary>The revision is switched off, or the master switch is.</summary>
    Disabled,
    /// <summary>After start and after every change of the revision, until the first probe or call succeeds (O13).</summary>
    Unknown,
    Available,
    /// <summary><see cref="AiAvailability.FailuresToOpen"/> failures in a row.</summary>
    Unavailable,
}

/// <param name="RevisionId">The revision the state belongs to; null when the kind is not configured.</param>
/// <param name="Since">When the kind became <see cref="AiProfileState.Available"/> or <see cref="AiProfileState.Unavailable"/>.</param>
/// <param name="LastErrorCode">The last failure's <see cref="AiOutcome"/> code since the revision was saved; never upstream text.</param>
/// <param name="LastCheckAt">The last verdict on the server: a probe, a successful call or a call failing with an availability code.</param>
/// <param name="LastCheckFailed">Whether that verdict was a failure; the probe then comes after 10 s instead of 60 s.</param>
/// <param name="ServedModel">Gateway: the model the server serves instead of the configured one, from the last good probe.</param>
public sealed record AiProfileHealth(string Kind, AiProfileState State, Guid? RevisionId, DateTime? Since, string? LastErrorCode,
    DateTime? LastSuccessAt, DateTime? LastCheckAt, bool LastCheckFailed, string? ServedModel = null);

/// <summary>
/// The circuit breaker of each profile kind (AI-25), in process memory (O16). It hears every model call through the pipeline
/// (<see cref="IAiOutcomeSink"/>) and every probe (<see cref="AiProbeRunner"/>): <see cref="FailuresToOpen"/> availability
/// failures in a row (<see cref="AiOutcome.IsAvailabilityFailure"/>: timeout, connection, 5xx, 429, auth, missing model, refused
/// redirect, unreadable key) make the kind unavailable, and the first success restores it. Atlas's own bad requests, unusable
/// answers and cancellations are no verdict on the server. A call or probe of another revision than the kind's current one
/// (a candidate index generation, a snapshot from before a save) never moves the state. The configuration cache hands every
/// loaded snapshot to <see cref="Sync"/>: a changed revision, or the master switch turned on, starts unverified.
/// </summary>
public sealed class AiAvailability : IAiOutcomeSink
{
    public const int FailuresToOpen = 2;
    private readonly Dictionary<string, Entry> m_entries = AiProfileKind.All.ToDictionary(kind => kind, _ => new Entry(), StringComparer.Ordinal);
    private readonly TimeProvider m_clock;
    private readonly ILogger<AiAvailability> m_logger;
    private readonly AiLiveSignals? m_signals;

    /// <param name="signals">Tells /hubs/live (capabilities on a state change, the administrators' health on every verdict) instead of
    /// the pages asking again; none in a host without it.</param>
    public AiAvailability(TimeProvider clock, ILogger<AiAvailability> logger, AiLiveSignals? signals = null)
    {
        m_clock = clock;
        m_logger = logger;
        m_signals = signals;
    }

    /// <summary>Aligns every kind with a loaded configuration. A kind whose revision or effective switch changed is reset:
    /// <see cref="AiProfileState.Unknown"/> when it may run, <see cref="AiProfileState.Disabled"/> or
    /// <see cref="AiProfileState.NotConfigured"/> otherwise; an unchanged kind keeps its state.</summary>
    public void Sync(AiConfigurationSnapshot configuration)
    {
        var reset = false;
        foreach (var (kind, entry) in m_entries)
        {
            var profile = configuration.Profile(kind);
            var active = profile is not null && configuration.Enabled && profile.Enabled;
            lock (entry)
            {
                if (entry.RevisionId == profile?.RevisionId && entry.Active == active)
                {
                    continue;
                }
                entry.Reset(profile?.RevisionId, active,
                    profile is null ? AiProfileState.NotConfigured : active ? AiProfileState.Unknown : AiProfileState.Disabled);
                reset = true;
            }
        }
        if (reset)
        {
            m_signals?.CapabilitiesChanged();
        }
    }

    public AiProfileHealth Health(string kind)
    {
        if (!m_entries.TryGetValue(kind, out var entry))
        {
            return new AiProfileHealth(kind, AiProfileState.NotConfigured, null, null, null, null, null, false);
        }
        lock (entry)
        {
            return new AiProfileHealth(kind, entry.State, entry.RevisionId, entry.Since, entry.LastErrorCode, entry.LastSuccessAt, entry.LastCheckAt,
                entry.LastCheckFailed, entry.ServedModel);
        }
    }

    public bool IsAvailable(string kind) => m_entries.TryGetValue(kind, out var entry) && Volatile.Read(ref entry.StateValue) == (int)AiProfileState.Available;

    /// <summary>Whether this very revision is the kind's current one and available.</summary>
    public bool IsAvailable(AiProfileSnapshot profile)
    {
        if (!m_entries.TryGetValue(profile.Kind, out var entry))
        {
            return false;
        }
        lock (entry)
        {
            return entry.State == AiProfileState.Available && entry.RevisionId == profile.RevisionId;
        }
    }

    public void Success(AiProfileSnapshot profile) => Record(profile, null, m_clock.GetUtcNow().UtcDateTime);

    public void Failure(AiProfileSnapshot profile, string code)
    {
        if (AiOutcome.IsAvailabilityFailure(code))
        {
            Record(profile, code, m_clock.GetUtcNow().UtcDateTime);
        }
    }

    /// <summary>A probe's verdict. Unlike a call, any failed probe counts: it sends no content, so a refusal is the server's
    /// state, not Atlas's request. Only a cancelled probe (shutdown) says nothing. A good probe also sets or clears the
    /// served model (<see cref="AiProbeResult.ServedModel"/>); calls cannot tell, so they leave it.</summary>
    public void Probed(AiProfileSnapshot profile, AiProbeResult result, DateTime now)
    {
        if (result.Ok)
        {
            Record(profile, null, now, probe: true, servedModel: result.ServedModel);
        }
        else if (result.Code != AiOutcome.Canceled)
        {
            Record(profile, result.Code ?? AiOutcome.Connection, now);
        }
    }

    private void Record(AiProfileSnapshot profile, string? failure, DateTime now, bool probe = false, string? servedModel = null)
    {
        if (!m_entries.TryGetValue(profile.Kind, out var entry))
        {
            return;
        }
        AiProfileState? changed = null;
        lock (entry)
        {
            if (!entry.Active || entry.RevisionId != profile.RevisionId)
            {
                return;
            }
            entry.LastCheckAt = now;
            entry.LastCheckFailed = failure is not null;
            if (failure is null)
            {
                entry.Failures = 0;
                entry.LastSuccessAt = now;
                if (probe)
                {
                    entry.ServedModel = servedModel;
                }
                if (entry.State != AiProfileState.Available)
                {
                    changed = entry.Become(AiProfileState.Available, now);
                }
            }
            else
            {
                entry.Failures++;
                entry.LastErrorCode = failure;
                if (entry.Failures >= FailuresToOpen && entry.State != AiProfileState.Unavailable)
                {
                    changed = entry.Become(AiProfileState.Unavailable, now);
                }
            }
        }
        if (changed is null)
        {
            // A probe or a failure is news for the health page; a successful call of a working profile (most calls) is not.
            if (probe || failure is not null)
            {
                m_signals?.HealthChanged();
            }
        }
        else
        {
            m_signals?.CapabilitiesChanged();
        }
        if (changed == AiProfileState.Unavailable)
        {
            m_logger.LogWarning("The {Kind} AI profile is unavailable after {Failures} failures in a row: {Outcome}", profile.Kind, FailuresToOpen, failure);
        }
        else if (changed == AiProfileState.Available)
        {
            m_logger.LogInformation("The {Kind} AI profile is available", profile.Kind);
        }
    }

    private sealed class Entry
    {
        public int StateValue = (int)AiProfileState.NotConfigured;
        public Guid? RevisionId;
        public bool Active;
        public int Failures;
        public DateTime? Since;
        public string? LastErrorCode;
        public DateTime? LastSuccessAt;
        public DateTime? LastCheckAt;
        public bool LastCheckFailed;
        public string? ServedModel;

        public AiProfileState State => (AiProfileState)StateValue;

        public AiProfileState Become(AiProfileState state, DateTime now)
        {
            Volatile.Write(ref StateValue, (int)state);
            Since = now;
            return state;
        }

        public void Reset(Guid? revisionId, bool active, AiProfileState state)
        {
            RevisionId = revisionId;
            Active = active;
            Volatile.Write(ref StateValue, (int)state);
            Failures = 0;
            Since = null;
            LastErrorCode = null;
            LastSuccessAt = null;
            LastCheckAt = null;
            LastCheckFailed = false;
            ServedModel = null;
        }
    }
}
