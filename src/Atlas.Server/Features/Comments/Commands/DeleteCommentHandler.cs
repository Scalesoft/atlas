using Atlas.Server.Features.Comments.Domain;
using Atlas.Server.Features.Comments.Persistence;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Comments.Commands;

/// <summary>Deletes a comment as its author or a manager of the object, also with the space's comments off: the row stays as the
/// placeholder; its text, place, mentions and search document go.</summary>
public sealed class DeleteCommentHandler
{
    private readonly ISession m_session;
    private readonly CommentGate m_gate;
    private readonly CommentNotices m_notices;

    public DeleteCommentHandler(ISession session, CommentGate gate, CommentNotices notices)
    {
        m_session = session;
        m_gate = gate;
        m_notices = notices;
    }

    public async Task Handle(DeleteComment command, CancellationToken ct)
    {
        var target = await m_gate.OpenAsync(command.ObjectId, ct);
        var comment = await m_gate.LockCommentAsync(target, command.CommentId, ct);
        if (comment.IsDeleted)
        {
            throw CommentErrors.Deleted();
        }
        if (!comment.MayDelete(target.Actor.Id, target.Manages))
        {
            throw CommentErrors.NotYours();
        }
        comment.Delete(target.Actor.Id, DateTime.UtcNow);
        await CommentWrites.DeriveAsync(m_session, comment, ct);
        await m_notices.AnnounceAsync(target.Envelope, ct);
    }
}
