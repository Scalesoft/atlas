using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.TreeReferences.Commands;

/// <summary>
/// Reference nodes with the discipline of the page organization (as BoardTreeHandler): the shared content lock, the space FOR UPDATE,
/// the account, then the node; the rules of the new and the current parent (PageParent) and the one sibling order (PageSiblings). Every change
/// needs Writer in the node's space (a reader knows the node: 403; no role: 404). Placing one also needs the target readable to its
/// creator, by the tree's own rule (else the 404 of a missing one). On MCP the space and the target's space must take part in AI (G5).
/// </summary>
public sealed class TreeReferenceHandler
{
    private const int MaxAnchor = 200;

    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly OrganizationLocks m_locks;
    private readonly ContentParticipants m_participants;
    private readonly TreeReferenceContent m_content;
    private readonly McpVisibility m_visibility;
    private readonly IRequestChannel m_channel;

    public TreeReferenceHandler(ISession session, IContentAccess access, OrganizationLocks locks, ContentParticipants participants, TreeReferenceContent content,
        McpVisibility visibility, IRequestChannel channel)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_participants = participants;
        m_content = content;
        m_visibility = visibility;
        m_channel = channel;
    }

    public async Task<PageTreeItem> Handle(AddTreeReference command, CancellationToken ct)
    {
        var request = command.Request;
        var anchor = Anchor(request.Anchor);
        var actor = await m_locks.LockForWriteAsync(command.SpaceId, ct);
        await RequireWriterAsync(command.SpaceId, ct);
        // On MCP a target in a space switched out of AI is as unreadable as any other (RelationNodes).
        var target = await m_content.ReadableTargetAsync(actor, request.TargetId, ct) ?? throw MissingTarget();
        await PageParent.ValidateNodeAsync(m_session, m_access, command.SpaceId, request.ParentId, ct);
        var reference = new TreeReference
        {
            Id = Guid.NewGuid(), SpaceId = command.SpaceId, ParentPageId = request.ParentId, TargetId = request.TargetId, Anchor = anchor,
            CreatedBy = actor.Id, CreatedAt = DateTime.UtcNow
        };
        reference.SortOrder = await PageSiblings.PositionAsync(m_session, m_participants.Tree, command.SpaceId, reference.Id, request.ParentId, request.BeforeId, actor, ct);
        await m_session.SaveAsync(reference, ct);
        await m_session.FlushAsync(ct);
        return PageTreeItem.Of(TreeReferenceContent.Node(reference.Id, reference.ParentPageId, reference.SortOrder, reference.TargetId, anchor, target.TypeId,
            target.Title, true), reference.ParentPageId);
    }

    public async Task Handle(RemoveTreeReference command, CancellationToken ct)
    {
        var (reference, _) = await LockAsync(command.ReferenceId, ct);
        await m_session.DeleteAsync(reference, ct);
        await m_session.FlushAsync(ct);
    }

    public async Task<OrganizationChange> Handle(MoveTreeReference command, CancellationToken ct)
    {
        var request = command.Request;
        var (reference, actor) = await LockAsync(command.ReferenceId, ct);
        await TreeVersion.ExpectAsync(m_session, reference.SpaceId, request.ExpectedTreeVersion, ct);
        await PageParent.ValidateNodeAsync(m_session, m_access, reference.SpaceId, request.ParentId, ct);
        var cancelled = await TitleOrder.HandMoveAsync(m_session, m_participants.Tree, actor, reference.SpaceId, reference.ParentPageId, request.ParentId, ct);
        reference.SortOrder = await PageSiblings.PositionAsync(m_session, m_participants.Tree, reference.SpaceId, reference.Id, request.ParentId, request.BeforePageId, actor, ct);
        reference.ParentPageId = request.ParentId;
        await m_session.FlushAsync(ct);
        return new OrganizationChange(await TreeVersion.ReadAsync(m_session, reference.SpaceId, ct), [reference.Id], TitleOrderCancelled: cancelled);
    }

    private async Task<(TreeReference Reference, UserAccount Actor)> LockAsync(Guid referenceId, CancellationToken ct)
    {
        // Lock order: the shared advisory before anything reads content; a node changes space only with its page's move, which holds the
        // space FOR UPDATE too, so the space read first is checked again under the lock.
        var spaceId = await m_session.QueryOver<TreeReference>().Where(x => x.Id == referenceId).Select(x => x.SpaceId).SingleOrDefaultAsync<Guid>(ct);
        if (spaceId == Guid.Empty)
        {
            throw Missing();
        }
        var actor = await m_locks.LockForWriteAsync(spaceId, ct);
        await RequireWriterAsync(spaceId, ct);
        var reference = await m_session.GetForUpdateAsync<TreeReference>(referenceId, ct);
        if (reference is not { IsTrashed: false } || reference.SpaceId != spaceId)
        {
            throw Missing();
        }
        await PageParent.RequireCurrentAsync(m_session, m_access, reference.ParentPageId, Missing(), ct);
        return (reference, actor);
    }

    private async Task RequireWriterAsync(Guid spaceId, CancellationToken ct)
    {
        SpaceRole role;
        try
        {
            role = await m_access.RequireSpaceAsync(spaceId, SpaceRole.Reader, ct);
            if (m_channel.Current.IsAiBound)
            {
                await m_visibility.RequireSpaceAsync(spaceId, ct);
            }
        }
        catch (ApiException ex) when (ex.Status == 404)
        {
            throw Missing();
        }
        if (role < SpaceRole.Writer)
        {
            throw new ApiException(403, "Strom tohoto prostoru může měnit jen ten, kdo v něm smí psát.");
        }
    }

    /// <summary>A heading or block id: letters, digits, '-', '_', ':' and '.', at most 200; blank is none.</summary>
    private static string? Anchor(string? anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor))
        {
            return null;
        }
        return anchor.Length <= MaxAnchor && anchor.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.')
            ? anchor
            : throw new ApiException(400, "Místo v cíli odkazu není platné.", "invalid_anchor");
    }

    private static ApiException Missing() => new(404, "Odkaz ve stromu nebyl nalezen.");

    private static ApiException MissingTarget() => new(404, "Cíl odkazu nebyl nalezen.", "target_not_found");
}
