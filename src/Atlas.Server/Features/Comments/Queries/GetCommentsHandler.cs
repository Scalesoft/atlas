using Atlas.Server.Features.Comments.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Comments.Queries;

/// <summary>A page of an object's comments for its reader (comments spec §3): newest first, twenty, the cursor the last item's id;
/// the same statements whatever the page holds.</summary>
public sealed class GetCommentsHandler
{
    public const int PageSize = 20;

    private readonly ISession m_session;
    private readonly CommentGate m_gate;

    public GetCommentsHandler(ISession session, CommentGate gate)
    {
        m_session = session;
        m_gate = gate;
    }

    public async Task<CommentPage> Handle(GetComments query, CancellationToken ct)
    {
        var target = await m_gate.OpenAsync(query.ObjectId, ct);
        var rows = await CommentReads.PageAsync(m_session, target.Envelope.Id, query.Before, PageSize + 1, ct);
        var count = await CommentReads.CountAsync(m_session, target.Envelope.Id, ct);
        return new CommentPage(rows.Take(PageSize).Select(row => target.Dto(row.Comment, row.AuthorName)).ToArray(), rows.Count > PageSize, count.Count,
            target.MayPost);
    }
}
