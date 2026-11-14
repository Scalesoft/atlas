namespace Atlas.Server.Platform.Resources;

/// <summary>
/// Another content type's part in what the mandatory pages module owns (interaction spec §6): the space tree, the personal
/// library and search. The pages module calls the participants of the enabled modules and never knows their types; a disabled
/// module registers none, so its rows stay where they are and simply do not show. Resolved at run time from the request scope
/// (a factory, so Wolverine's generated handler code is the same with any set of modules on).
/// </summary>
public sealed class ContentParticipants
{
    private readonly IServiceProvider m_services;

    public ContentParticipants(IServiceProvider services)
    {
        m_services = services;
    }

    public IReadOnlyList<IContentTreeParticipant> Tree => m_services.GetServices<IContentTreeParticipant>().ToArray();

    public IReadOnlyList<IContentLibraryParticipant> Library => m_services.GetServices<IContentLibraryParticipant>().ToArray();

    public IReadOnlyList<IContentSearchParticipant> Search => m_services.GetServices<IContentSearchParticipant>().ToArray();

    public IReadOnlyList<IContentReferenceParticipant> References => m_services.GetServices<IContentReferenceParticipant>().ToArray();

    public IReadOnlyList<IObjectRelationSource> Relations => m_services.GetServices<IObjectRelationSource>().ToArray();

    /// <summary>Every module's, enabled or not (IAtlasModule.AddPurge).</summary>
    public IReadOnlyList<IContentPurgeParticipant> Purge => m_services.GetServices<IContentPurgeParticipant>().ToArray();
}

/// <summary>
/// A module's rows of permanently deleted pages (unified trash spec §2), registered whether the module is enabled or not
/// (IAtlasModule.AddPurge, ARCH3-2): a disabled module's data stays in the database, so a purge must still remove it. It needs nothing
/// but the session. Runs inside the pages module's transaction, after its locks and before the pages become tombstones, in a constant
/// number of statements.
/// </summary>
public interface IContentPurgeParticipant
{
    /// <summary>The key of the audit row's Detail that counts the objects of its own going with the pages (<c>boards</c>); null when
    /// it removes only derived rows.</summary>
    string? Counted { get; }

    /// <summary>Its rows under <paramref name="pageIds"/>: with a batch, the nodes that went to the trash with it, a node bound to its
    /// page whatever its batch, and a live node under them (one the module, switched off then, did not trash); with a null batch the
    /// whole space goes, so every row of the space. Returns the number of its objects deleted.</summary>
    /// <param name="actorId">Who purges: the audit of what it closes names them.</param>
    Task<int> PurgeAsync(Guid spaceId, IReadOnlyCollection<Guid> pageIds, Guid? batchId, Guid actorId, CancellationToken ct);
}

/// <summary>
/// A node type of the space tree besides pages (a board). Its nodes are leaves under a page or at the space root and share the
/// pages' sibling order (SortOrder, gaps of 1024, ties by id). Every method runs inside the pages module's transaction, after its
/// locks (content shared, the space FOR UPDATE for a change, the account), and answers with a constant number of queries.
/// </summary>
public interface IContentTreeParticipant
{
    /// <summary>Its live nodes of the space the account may list, given the account's role in the space (null: page grants only).</summary>
    Task<IReadOnlyList<ContentTreeNode>> NodesAsync(Guid spaceId, UserAccount actor, SpaceRole? spaceRole, CancellationToken ct);

    /// <summary>Its live nodes under <paramref name="parentPageId"/> (null: the space root), for a position among all siblings.</summary>
    Task<IReadOnlyList<ContentTreeSibling>> SiblingsAsync(Guid spaceId, Guid? parentPageId, CancellationToken ct);

    /// <summary>New sort orders of some of its nodes, when a level's gaps are exhausted and the whole level is renumbered.</summary>
    Task ReorderAsync(IReadOnlyDictionary<Guid, int> sortOrders, CancellationToken ct);

    /// <summary>A page subtree goes to the trash: its live nodes under these pages join the same batch; with
    /// <paramref name="spaceRoot"/> (the whole space goes, unified trash spec rule 4) its nodes at the space root too.</summary>
    Task TrashAsync(Guid spaceId, IReadOnlyCollection<Guid> pageIds, bool spaceRoot, ContentTrashStamp stamp, CancellationToken ct);

    /// <summary>Pages of a trash batch come back: its nodes under them (and at the space root with <paramref name="spaceRoot"/>)
    /// that went with that batch come back too.</summary>
    Task RestoreAsync(Guid spaceId, IReadOnlyCollection<Guid> pageIds, bool spaceRoot, Guid batchId, CancellationToken ct);

    /// <summary>Pages move to another space: its nodes under them, trashed or not, move along.</summary>
    Task MoveSpaceAsync(Guid sourceSpaceId, Guid targetSpaceId, IReadOnlyCollection<Guid> pageIds, CancellationToken ct);

    /// <summary>Its trashed nodes of the space, newest first, at most <paramref name="take"/>, for the space's trash.</summary>
    Task<IReadOnlyList<ContentTrashItem>> TrashedAsync(Guid spaceId, int take, CancellationToken ct);
}

/// <summary>A node of the tree: its type (<c>board</c>, <c>reference</c>), its parent page (null: the root; a parent the account cannot
/// see is dropped by the tree, D8) and whether the account may change it. A reference node also names its target (TargetId, its type and
/// the anchor inside it); a target the account may not read is <c>Readable = false</c> with no title and no type.</summary>
public sealed record ContentTreeNode(string Type, Guid Id, Guid? ParentId, string? Title, int SortOrder, bool CanEdit)
{
    /// <summary>The type of a node that is no row of its own: an object attached to its parent page (a page's Purpose board), shown
    /// as the page's quiet mark to whoever may edit the page (spec 2026-10-27 §5.1), and to whoever may read it when <c>Readable</c>.</summary>
    public const string Attached = "attached";

    public Guid? TargetId { get; init; }
    public string? TargetTypeId { get; init; }
    public string? Anchor { get; init; }
    public bool Readable { get; init; } = true;
}

public sealed record ContentTreeSibling(Guid Id, int SortOrder);

public sealed record ContentTrashStamp(Guid BatchId, DateTime TrashedAt, Guid TrashedById);

public sealed record ContentTrashItem(string Type, Guid Id, Guid? ParentId, string Title, DateTime TrashedAt, Guid TrashedById, Guid TrashBatchId);

/// <summary>A content type in the personal library's favorites and recent visits (the PageFavorite and PageVisit rows name any
/// content resource). The library takes the locks of personal state; the participant decides only its type's read access.</summary>
public interface IContentLibraryParticipant
{
    /// <summary>The space of <paramref name="resourceId"/> when it is one of its resources, before any lock; else null.</summary>
    Task<Guid?> SpaceOfAsync(Guid resourceId, CancellationToken ct);

    /// <summary>After the personal locks: the account, when it may read the resource; otherwise the same 404 as a missing one.</summary>
    Task<UserAccount> RequireReadableAsync(Guid resourceId, CancellationToken ct);

    /// <summary>The account's favorites or recent visits (<paramref name="kind"/>) of its type that it may read, newest first.</summary>
    Task<IReadOnlyList<ContentLibraryEntry>> ListAsync(string kind, UserAccount actor, int take, CancellationToken ct);
}

/// <summary>A library row: OrderedAt is when it became a favorite or was last visited, the list's order.</summary>
public sealed record ContentLibraryEntry(string Type, Guid Id, Guid SpaceId, string SpaceName, string Title, DateTime OrderedAt, bool IsFavorite,
    DateTime? VisitedAt);

/// <summary>A content type in search: its resources the account may read that match the query (Search's normalization).</summary>
public interface IContentSearchParticipant
{
    Task<IReadOnlyList<ContentSearchHit>> SearchAsync(UserAccount actor, string query, string normalized, Guid? spaceId, int limit, CancellationToken ct);
}

/// <summary>A content type's titles for links rendered in pages (POST /api/pages/references): of <paramref name="ids"/> in one query, those
/// the account may open, with the title it would see; no body is read and no opening recorded.</summary>
public interface IContentReferenceParticipant
{
    Task<IReadOnlyList<ContentReference>> ResolveAsync(UserAccount actor, IReadOnlyCollection<Guid> ids, CancellationToken ct);
}

public sealed record ContentReference(string Type, Guid Id, string Title);

/// <param name="Target">Where a hit inside another object leads (a comment: its object and anchor); null: the hit is the resource.</param>
public sealed record ContentSearchHit(string Type, Guid Id, Guid SpaceId, string SpaceName, string Title, string Snippet, ContentSearchTarget? Target = null);

/// <summary>The object a hit leads to, by its type, and the place inside it (a heading or item id; null for the whole object).</summary>
public sealed record ContentSearchTarget(Guid ObjectId, string TypeId, string? Anchor);
