using Atlas.Server.Features.Comments.Contracts;
using Atlas.Server.Features.Comments.Domain;
using Atlas.Server.Features.Comments.Persistence;
using Atlas.Server.Features.Notifications;
using Atlas.Server.Platform.Events;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Comments.Commands;

/// <summary>The author's new text of a live comment („upraveno“), while the space's comments are on; no history is kept.</summary>
public sealed class EditCommentHandler
{
    private readonly ISession m_session;
    private readonly CommentGate m_gate;
    private readonly CommentNotices m_notices;
    private readonly Outbox m_outbox;

    public EditCommentHandler(ISession session, CommentGate gate, CommentNotices notices, Outbox outbox)
    {
        m_session = session;
        m_gate = gate;
        m_notices = notices;
        m_outbox = outbox;
    }

    public async Task<CommentDto> Handle(EditComment command, CancellationToken ct)
    {
        var target = await m_gate.OpenAsync(command.ObjectId, ct);
        var comment = await m_gate.LockCommentAsync(target, command.CommentId, ct);
        if (comment.IsDeleted)
        {
            throw CommentErrors.Deleted();
        }
        if (!target.MayPost)
        {
            throw CommentErrors.Disabled();
        }
        if (!comment.MayEdit(target.Actor.Id))
        {
            throw CommentErrors.NotYours();
        }
        comment.Edit(CommentText.Body(command.Body), DateTime.UtcNow);
        var mentioned = await CommentWrites.DeriveAsync(m_session, comment, ct);
        m_outbox.Notify(new NotificationRequested(NotificationKinds.CommentMention, target.Envelope.Id, target.Actor.Id, mentioned, comment.Anchor));
        await m_notices.AnnounceAsync(target.Envelope, ct);
        return target.Dto(comment, target.Actor.DisplayName);
    }
}
