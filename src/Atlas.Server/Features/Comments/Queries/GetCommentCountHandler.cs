using Atlas.Server.Features.Comments.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Comments.Queries;

/// <summary>The count of an object's live comments for its reader (the quiet badge), from the index.</summary>
public sealed class GetCommentCountHandler
{
    private readonly ISession m_session;
    private readonly CommentGate m_gate;

    public GetCommentCountHandler(ISession session, CommentGate gate)
    {
        m_session = session;
        m_gate = gate;
    }

    public async Task<CommentCount> Handle(GetCommentCount query, CancellationToken ct)
    {
        var target = await m_gate.OpenAsync(query.ObjectId, ct);
        return await CommentReads.CountAsync(m_session, target.Envelope.Id, ct);
    }
}
