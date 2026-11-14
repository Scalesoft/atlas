using Atlas.Server.Platform.Live;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Availability;

/// <summary>
/// <c>ai:{Guid.Empty}</c> on /hubs/live, for every signed-in account: <see cref="AiChanged"/> says that what <c>GET /api/ai/capabilities</c>
/// answers may have changed (a profile became available or unavailable, the settings changed). It carries nothing: each account reads its
/// own booleans over HTTP, so no provider, model, state or reason reaches anyone here.
/// </summary>
public sealed class AiLiveChannel(AtlasDatabase database) : AccountFeedChannel(database)
{
    public const string Prefix = "ai";

    public const string Event = "AiChanged";

    public static readonly string Name = NameOf(Prefix);

    public override string Kind => Prefix;

    protected override Task<bool> AllowsAsync(ISession session, UserAccount actor, CancellationToken ct) => Task.FromResult(true);
}

/// <summary>
/// <c>ai-admin:{Guid.Empty}</c>, for effective wiki administrators only, as <c>/api/admin/ai</c>: <c>AiHealthChanged</c> (an <see cref="AiChanged"/>) says that the
/// health of Správa wiki → AI may have changed (a verdict of a probe or a call, a state change, a settings save). It carries nothing; the
/// overview is read over HTTP.
/// </summary>
public sealed class AiAdminLiveChannel(AtlasDatabase database) : WikiAdminFeedChannel(database)
{
    public const string Prefix = "ai-admin";

    /// <summary>Its own name: a client routes an event to one channel.</summary>
    public const string Event = "AiHealthChanged";

    public static readonly string Name = NameOf(Prefix);

    public override string Kind => Prefix;
}

/// <summary>Server → client on <c>ai:</c> and <c>ai-admin:</c>: read again. Empty on purpose.</summary>
public sealed record AiChanged;

/// <summary>Tells the two AI feeds, throttled (<see cref="LiveThrottle"/>): what may change capabilities tells both, a verdict only the
/// administrators' health. Silent in a host without /hubs/live (a container of the AI services alone).</summary>
public sealed class AiLiveSignals(LiveRelay? relay = null, LiveThrottle? throttle = null)
{
    public void CapabilitiesChanged()
    {
        Post(AiLiveChannel.Name, AiLiveChannel.Event);
        HealthChanged();
    }

    public void HealthChanged() => Post(AiAdminLiveChannel.Name, AiAdminLiveChannel.Event);

    private void Post(string channel, string method)
    {
        if (relay is not null && throttle is not null)
        {
            throttle.Post(channel, () => relay.PublishAsync(channel, method, new AiChanged()));
        }
    }
}
