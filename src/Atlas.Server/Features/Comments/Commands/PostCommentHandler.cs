using Atlas.Server.Features.Comments.Contracts;
using Atlas.Server.Features.Comments.Domain;
using Atlas.Server.Features.Comments.Persistence;
using Atlas.Server.Features.Notifications;
using Atlas.Server.Platform.Events;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Comments.Commands;

/// <summary>A new comment on an object its author may read, with the space's comments on; its mentions and search document in the
/// same transaction, the object's view sessions told after the commit.</summary>
public sealed class PostCommentHandler
{
    private readonly ISession m_session;
    private readonly CommentGate m_gate;
    private readonly CommentNotices m_notices;
    private readonly Outbox m_outbox;

    public PostCommentHandler(ISession session, CommentGate gate, CommentNotices notices, Outbox outbox)
    {
        m_session = session;
        m_gate = gate;
        m_notices = notices;
        m_outbox = outbox;
    }

    public async Task<CommentDto> Handle(PostComment command, CancellationToken ct)
    {
        var target = await m_gate.OpenAsync(command.ObjectId, ct);
        if (!target.MayPost)
        {
            throw CommentErrors.Disabled();
        }
        var comment = new Comment
        {
            Id = Guid.NewGuid(), ObjectId = target.Envelope.Id, AuthorId = target.Actor.Id, Body = CommentText.Body(command.Body),
            Anchor = CommentText.Anchor(command.Anchor), CreatedAt = DateTime.UtcNow
        };
        await m_session.SaveAsync(comment, ct);
        var mentioned = await CommentWrites.DeriveAsync(m_session, comment, ct);
        m_outbox.Notify(new NotificationRequested(NotificationKinds.CommentMention, target.Envelope.Id, target.Actor.Id, mentioned, comment.Anchor));
        await m_notices.AnnounceAsync(target.Envelope, ct);
        return target.Dto(comment, target.Actor.DisplayName);
    }
}
