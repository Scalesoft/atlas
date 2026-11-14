using Atlas.Server.Features.SpaceAi.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.SpaceAi.Queries;

/// <summary>Space admins (and WikiAdmins) only, authorized like "Nenalezené dotazy": a member of the space gets 403, anyone else
/// the same 404 as for a space that does not exist, so the answer reveals nothing about spaces the actor cannot see.</summary>
public sealed class GetSpaceAiSettingsHandler(ISession session, IContentAccess access)
{
    public async Task<SpaceAiSettingsView> Handle(GetSpaceAiSettings query, CancellationToken ct)
    {
        await access.LockActorAsync(query.SpaceId, ct);
        await access.RequireSpaceAsync(query.SpaceId, SpaceRole.Admin, ct);
        return CurrentSpaceAi.ToView(await CurrentSpaceAi.LoadAsync(session, query.SpaceId, ct));
    }
}
