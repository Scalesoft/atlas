using Atlas.Server.Features.PersonalLibrary.Domain;
using Atlas.Server.Features.Spaces.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Spaces.Commands;

public sealed class SetSpacePinHandler
{
    private readonly ISession m_session;
    private readonly PersonalPageGuard m_guard;
    private readonly PageListing m_listing;

    public SetSpacePinHandler(ISession session, PersonalPageGuard guard, PageListing listing)
    {
        m_session = session;
        m_guard = guard;
        m_listing = listing;
    }

    public async Task<SpacePinState> Handle(SetSpacePin command, CancellationToken ct)
    {
        var spaceId = command.SpaceId;
        // The personal lock serializes two tabs of the account; the shared space lock keeps the policy stable against a settings save.
        var actor = await m_guard.LockIdentityAsync(spaceId, true, ct);
        await m_listing.RequireSpaceAsync(actor, spaceId, ct);
        var policy = (await m_session.GetAsync<Space>(spaceId, ct)).PinPolicy;
        if (!command.Pinned && policy == SpacePinPolicy.Required)
        {
            throw new ApiException(409, "Prostor připnul správce všem, odepnout ho nelze.", "space_pin_required");
        }
        var actorId = actor.Id;
        // A choice is only ever closed by its successor, so the current row is the latest one.
        var current = await m_session.QueryOver<SpacePin>()
            .Where(x => x.UserId == actorId && x.SpaceId == spaceId && x.ActiveTo == null)
            .SingleOrDefaultAsync(ct);
        if (SpacePin.Effective(policy, current?.Pinned) == command.Pinned)
        {
            return new SpacePinState(command.Pinned);
        }
        var now = current is null ? DateTime.UtcNow : Temporal.NextBoundary(current.ActiveFrom, DateTime.UtcNow);
        if (current is not null)
        {
            current.ActiveTo = now;
            // NHibernate flushes inserts before updates: close the current choice first, or the new one overlaps it.
            await m_session.FlushAsync(ct);
        }
        await m_session.SaveAsync(new SpacePin { Id = Guid.NewGuid(), UserId = actorId, SpaceId = spaceId, Pinned = command.Pinned, ActiveFrom = now }, ct);
        await m_session.FlushAsync(ct);
        return new SpacePinState(command.Pinned);
    }
}
