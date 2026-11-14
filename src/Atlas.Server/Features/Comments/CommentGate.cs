using Atlas.Server.Features.Comments.Contracts;
using Atlas.Server.Features.Comments.Domain;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Comments;

/// <summary>
/// The object a comment message is about, decided once per message (comments spec §2): whoever may read the object reads and
/// writes its comments, through the object's type in the registry (a missing, trashed or unreadable object, or one of a type no
/// enabled module registers, is the same 404: fail closed). Writing also needs the space's „Komentáře“ switch on; who manages the
/// object (a WikiAdmin, the space's Admin, a private object's owner) may delete anyone's comment.
/// </summary>
public sealed class CommentGate
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly ObjectTypes m_types;
    private readonly SpaceRoles m_roles;

    public CommentGate(ISession session, IContentAccess access, ObjectTypes types, SpaceRoles roles)
    {
        m_session = session;
        m_access = access;
        m_types = types;
        m_roles = roles;
    }

    public async Task<CommentTarget> OpenAsync(Guid objectId, CancellationToken ct)
    {
        var spaceId = await m_session.QueryOver<ContentResource>().Where(x => x.Id == objectId).Select(x => x.SpaceId).SingleOrDefaultAsync<Guid?>(ct);
        var actor = await m_access.LockActorAsync(spaceId, ct);
        if (await m_session.GetAsync<ContentResource>(objectId, ct) is not { TrashedAt: null } envelope || !await MayReadAsync(actor, envelope.Id, envelope.TypeId, ct))
        {
            throw CommentErrors.ObjectNotFound();
        }
        var manages = await m_roles.IsWikiAdminAsync(actor, ct) || envelope.Scope switch
        {
            ObjectScopes.Private => envelope.OwnerId == actor.Id,
            ObjectScopes.Space when envelope.SpaceId is { } space => await m_roles.EffectiveAsync(actor, space, ct) == SpaceRole.Admin,
            _ => false
        };
        var mayPost = envelope.SpaceId is not { } own
            || await m_session.QueryOver<Space>().Where(x => x.Id == own).Select(x => x.CommentsEnabled).SingleOrDefaultAsync<bool>(ct);
        return new CommentTarget(actor, envelope, manages, mayPost);
    }

    /// <summary>Whether the account may read a live object of the type, as the type decides; false for a type no enabled module registers.</summary>
    public async Task<bool> MayReadAsync(UserAccount actor, Guid objectId, string typeId, CancellationToken ct)
    {
        return m_types.Find(typeId) is { } type && await type.AuthorizeAsync(new ObjectActor(actor), objectId, ct) != ObjectAccess.None;
    }

    /// <summary>A comment of the target's object, FOR UPDATE, so edits and the deletion of one comment serialize.</summary>
    public async Task<Comment> LockCommentAsync(CommentTarget target, Guid commentId, CancellationToken ct)
    {
        return await m_session.GetAsync<Comment>(commentId, LockMode.Upgrade, ct) is { } comment && comment.ObjectId == target.Envelope.Id
            ? comment
            : throw CommentErrors.NotFound();
    }
}

/// <summary>The decision of <see cref="CommentGate"/>: the locked account, the object's envelope, whether the account manages the
/// object and whether it may post (and edit) now.</summary>
public sealed record CommentTarget(UserAccount Actor, ContentResource Envelope, bool Manages, bool MayPost)
{
    /// <summary>The comment as this reader sees it, with what the reader may do with it.</summary>
    public CommentDto Dto(Comment comment, string authorName) => new(comment.Id, comment.AuthorId, authorName, comment.Body, comment.Anchor,
        comment.CreatedAt, comment.EditedAt, comment.IsDeleted, MayPost && comment.MayEdit(Actor.Id), comment.MayDelete(Actor.Id, Manages));
}
