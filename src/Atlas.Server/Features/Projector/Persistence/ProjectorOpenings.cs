using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Projector.Persistence;

/// <summary>
/// The openings Projector's reads record, one PageAccessEvent per page they returned content of, in one statement (as GET of a page
/// records one). Only for a token or the assistant: the web's screens refresh while open and the user sees only what they open, while an
/// agent's read is what the page's audit must show (review A5).
/// </summary>
public sealed class ProjectorOpenings(ISession session, IRequestChannel channel)
{
    public bool Records => channel.Current.Channel != ContentChannel.Web;

    public async Task RecordAsync(Guid actorId, IEnumerable<(Guid PageId, Guid RevisionId)> pages, CancellationToken ct)
    {
        var current = channel.Current;
        var opened = Records ? pages.Distinct().ToArray() : [];
        if (opened.Length == 0)
        {
            return;
        }
        await session.CreateSQLQuery("""
                INSERT INTO "PageAccessEvent"("PageId","RevisionId","ActorId","Action","Channel","ApiTokenId")
                SELECT x."PageId",x."RevisionId",:actor,'page.opened',:channel,:token
                FROM unnest(CAST(string_to_array(:pages,',') AS uuid[]),CAST(string_to_array(:revisions,',') AS uuid[])) AS x("PageId","RevisionId")
                """)
            .SetParameter("actor", actorId).SetParameter("channel", current.Code).SetParameter("token", current.ApiTokenId, NHibernateUtil.Guid)
            .SetParameter("pages", string.Join(",", opened.Select(x => x.PageId))).SetParameter("revisions", string.Join(",", opened.Select(x => x.RevisionId)))
            .ExecuteUpdateAsync(ct);
    }
}
