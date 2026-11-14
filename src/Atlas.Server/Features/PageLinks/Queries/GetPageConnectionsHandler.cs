using Atlas.Server.Features.PageLinks.Contracts;

namespace Atlas.Server.Features.PageLinks.Queries;

/// <summary>Connections of a page the actor may read. Not offered to external sharing, the personal API or history: only the
/// signed-in /api/pages route maps it.</summary>
public sealed class GetPageConnectionsHandler(PageGate gate, PageLinkStore store, Atlas.Server.Features.Ai.Access.AiPolicyResolver policy)
{
    /// <summary>Related pages shown below the content (design §7.3 R1: 3–5).</summary>
    public const int RelatedLimit = 5;

    public async Task<PageConnections> Handle(GetPageConnections query, CancellationToken ct)
    {
        if (query.Limit is < 1 or > 50)
        {
            throw new ApiException(400, "Počet odkazů musí být 1–50.");
        }
        // ReadContent includes the private draft rule; a target the actor may not read is missing, never forbidden.
        return await ReadAsync((await gate.ReadAsync(query.PageId, AccessOperation.ReadContent, ct)).Require(), query, ct);
    }

    /// <summary>The connections of a page opened for ReadContent earlier in this transaction (PageBundle).</summary>
    public async Task<PageConnections> ReadAsync(LockedPage opened, GetPageConnections query, CancellationToken ct)
    {
        var (actor, page) = (opened.Actor, opened.Page);
        var linksHere = await store.LinksHereAsync(actor, page, query.Limit, ct);
        if (!query.Related || !(await policy.ResolveAsync(actor.Id, ct)).Enabled)
        {
            return new PageConnections(linksHere);
        }
        var related = await store.RelatedAsync(actor, page, RelatedLimit, ct);
        return new PageConnections(linksHere) { Related = new RelatedPageList(related) { People = await store.RelatedPeopleAsync(related, ct) } };
    }
}
