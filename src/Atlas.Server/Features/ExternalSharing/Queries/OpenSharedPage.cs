using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.ExternalSharing.Contracts;
using Atlas.Server.Features.PageLinks;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ExternalSharing.Queries;

// Only one-way hashes cross Wolverine's message boundary, never bearer secrets or token URLs.
public sealed record OpenSharedPage(string TokenHash);
public sealed class OpenSharedPageHandler(ISession session, ExternalSharingAccess access, Pages.Domain.RequirementPageConversion conversion)
{
    public async Task<SharedPageDto> Handle(OpenSharedPage query, CancellationToken ct)
    {
        var link = await access.RequirePublicAsync(query.TokenHash, ct);
        var shown = await access.SharedRevisionAsync(link, ct);
        var revision = await session.GetAsync<PageRevision>(shown, ct);
        if (revision is null || revision.PageId != link.PageId) throw ExternalSharingAccess.Missing();
        // 3.6 repair on open (a public reader too): a page still holding requirement blocks is converted first.
        if (await conversion.RepairOnOpenAsync(link.PageId, revision.Id, revision.ContentJson, ct)) await session.RefreshAsync(revision, ct);
        var publishedAt = await session.QueryOver<PagePublication>()
            .Where(x => x.PageId == link.PageId && x.RevisionId == link.RevisionId)
            .Select(Projections.Min<PagePublication>(x => x.ActiveFrom)).SingleOrDefaultAsync<DateTime?>(ct);
        if (publishedAt is null) throw ExternalSharingAccess.Missing();
        // A live page shows its latest save, dated by it.
        if (shown != link.RevisionId) publishedAt = revision.ActiveFrom;
        await PageAccess.PageAccessAudit.RecordOpeningAsync(session, link.PageId, revision.Id, null, link.Id, null, ct);
        var content = SharedDocument.ForPublic(revision.ContentJson, await SharedPagesAsync(revision.ContentJson, link.SpaceId, ct));
        return new SharedPageDto(revision.Title, content, revision.Version, publishedAt.Value, link.ExpiresAt, shown != link.RevisionId);
    }

    /// <summary>
    /// The linked pages whose title a public reader may see, with their published title: those every reader of the shared page's space
    /// reads (published, not in the trash, no own access, no template). Other links, boards included, show no title (GAP1-2).
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string>> SharedPagesAsync(string contentJson, Guid spaceId, CancellationToken ct)
    {
        var targets = PageLinkTargets.Of(contentJson);
        if (targets.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }
        var rows = await session.CreateSQLQuery("""
            SELECT p."Id",published."Title" FROM "Page" p
            JOIN "PageRevision" published ON published."Id"=p."PublishedRevisionId"
            WHERE p."Id"=ANY(CAST(string_to_array(:targets, ',') AS uuid[])) AND p."SpaceId"=:space AND NOT p."IsTrashed" AND NOT p."IsTemplate" AND NOT p."OwnAccess"
            """)
            .AddScalar("Id", NHibernateUtil.Guid).AddScalar("Title", NHibernateUtil.String)
            .SetParameter("targets", string.Join(',', targets)).SetParameter("space", spaceId)
            .ListAsync<object[]>(ct);
        return rows.ToDictionary(row => (Guid)row[0], row => (string)row[1]);
    }
}
