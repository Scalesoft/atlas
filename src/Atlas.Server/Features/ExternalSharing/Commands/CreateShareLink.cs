using Atlas.Server.Features.ExternalSharing.Contracts;
using Atlas.Server.Features.ExternalSharing.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ExternalSharing.Commands;

public sealed record CreateShareLink(Guid PageId, CreateShareLinkRequest Request);
public sealed class CreateShareLinkHandler(ISession session, ExternalSharingAccess access, ShareLinkReader reader)
{
    public async Task<IssuedShareLink> Handle(CreateShareLink command, CancellationToken ct)
    {
        var (page, actor) = await access.RequirePageShareAsync(command.PageId, true, ct);
        var name = Rules.Required(command.Request.Name, "Název odkazu", 100);
        var now = Temporal.NextBoundary(DateTime.UnixEpoch, DateTime.UtcNow);
        var expiry = command.Request.ExpiresAt.UtcDateTime;
        if (expiry <= now || expiry > now.AddDays(PageShareLink.MaxValidityDays))
            throw new ApiException(400, "Platnost odkazu musí být v budoucnosti, nejvýše za {maxValidityDays} dní.", args: new Dictionary<string, object?> { ["maxValidityDays"] = PageShareLink.MaxValidityDays });
        if (page.IsTrashed || page.PublishedRevisionId is null)
            throw new ApiException(409, "Sdílet lze pouze publikovanou stránku mimo koš.");
        var policy = await session.QueryOver<SpaceExternalSharingPolicy>()
            .Where(x => x.SpaceId == page.SpaceId && x.ActiveTo == null).SingleOrDefaultAsync(ct);
        if (policy is not { Enabled: true })
            throw new ApiException(409, "Externí sdílení je v tomto prostoru vypnuté.");
        if (await session.QueryOver<PageShareLink>().Where(x => x.PageId == page.Id && x.ActiveTo == null && x.ExpiresAt > now).RowCountAsync(ct) >= 20)
            throw new ApiException(409, "Stránka může mít nejvýše 20 aktivních odkazů. Nejprve některý zrušte.");
        if (!await access.CanShareUntilAsync(actor, page, expiry, ct))
            throw new ApiException(403, "Odkaz nelze sdílet na dobu delší, než je platnost vašeho oprávnění správce.");
        var secret = ShareSecret.Create();
        var link = new PageShareLink { Id = Guid.NewGuid(), SpaceId = page.SpaceId, PageId = page.Id,
            RevisionId = page.PublishedRevisionId.Value, PolicyId = policy.Id, CreatedById = actor.Id,
            CreatorSecurityStamp = actor.SecurityStamp, Name = name, TokenHash = ShareSecret.Hash(secret), ActiveFrom = now, ExpiresAt = expiry,
            FollowsLive = await PublicationModes.EffectiveAsync(session, page, ct) == PublicationMode.Live };
        await session.SaveAsync(link, ct);
        await ShareRevocation.RecordAsync(session, "share.created", link.Id, link.PageId, link.RevisionId, actor.Id, ct);
        var result = await reader.ReadAsync(link.SpaceId, link.PageId, link.Id, 0, 1, ct);
        return new IssuedShareLink(result.Items.Single(), secret);
    }
}
