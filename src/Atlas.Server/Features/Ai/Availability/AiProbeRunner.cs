using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Domain;

namespace Atlas.Server.Features.Ai.Availability;

/// <summary>
/// One round of availability probes (AI-25), the testable logic of <see cref="AiProbeService"/>. A configured, enabled profile
/// is probed at once while unverified, <see cref="RetryInterval"/> after a failed check (so an unavailable profile every 10 s: back within seconds of the server, AI4-11)
/// and <see cref="CheckInterval"/> after a good one, even without traffic; a call counts as a check. A probe is
/// <see cref="IAiClientFactory.ProbeAsync"/>: the model list, no content, no tokens, at most 5 s. Profiles that are not
/// configured or are switched off are never probed. Nothing here throws for an outage.
/// </summary>
public sealed class AiProbeRunner(AiConfigurationCache cache, AiAvailability availability, IAiClientFactory factory, ILogger<AiProbeRunner> logger)
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    /// <returns>The number of profiles probed.</returns>
    public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var configuration = await cache.GetAsync(ct);
        var due = new List<AiProfileSnapshot>();
        foreach (var kind in AiProfileKind.All)
        {
            var profile = configuration.Profile(kind);
            var health = availability.Health(kind);
            // Borrowed quick profiles have no connection of their own; chat is probed once above.
            if (profile is not null && !profile.UseChatProfile && health.RevisionId == profile.RevisionId && IsDue(health, now.UtcDateTime))
            {
                due.Add(profile);
            }
        }
        await Task.WhenAll(due.Select(profile => ProbeAsync(profile, now.UtcDateTime, ct)));
        return due.Count;
    }

    public static bool IsDue(AiProfileHealth health, DateTime now)
        => health.State is AiProfileState.Unknown or AiProfileState.Available or AiProfileState.Unavailable
            && (health.LastCheckAt is not { } last || now - last >= (health.LastCheckFailed ? RetryInterval : CheckInterval));

    private async Task ProbeAsync(AiProfileSnapshot profile, DateTime now, CancellationToken ct)
    {
        AiProbeResult result;
        try
        {
            result = await factory.ProbeAsync(profile, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            // The factory reports outages as results; anything thrown is unexpected. Its type only: a message could carry upstream text.
            logger.LogWarning("Probing the {Kind} AI profile at {Host} failed unexpectedly ({ErrorType})", profile.Kind, profile.Host, exception.GetType().Name);
            result = new AiProbeResult(false, AiOutcome.Connection, false);
        }
        availability.Probed(profile, result, now);
    }
}
