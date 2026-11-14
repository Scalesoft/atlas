using Atlas.Server.Features.ExternalSharing.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ExternalSharing.Commands;

public sealed record RevokeShareLink(Guid PageId, Guid LinkId);
public sealed class RevokeShareLinkHandler(ISession session, ExternalSharingAccess access)
{
    public async Task Handle(RevokeShareLink command, CancellationToken ct)
    {
        var (page, actor) = await access.RequirePageShareAsync(command.PageId, true, ct);
        var link = await session.GetAsync<PageShareLink>(command.LinkId, ct);
        if (link is null || link.PageId != page.Id || link.SpaceId != page.SpaceId)
            throw ExternalSharingAccess.Missing();
        if (link.ActiveTo is null)
        {
            link.ActiveTo = Temporal.NextBoundary(link.ActiveFrom, DateTime.UtcNow);
            await ShareRevocation.RecordAsync(session, "share.revoked", link.Id, link.PageId, link.RevisionId, actor.Id, ct);
        }
    }
}
