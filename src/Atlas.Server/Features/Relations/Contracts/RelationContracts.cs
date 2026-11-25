namespace Atlas.Server.Features.Relations.Contracts;

/// <summary>A page of a space's contents (spec 2026-10-27 §13.3). <see cref="Summary"/> counts the whole space the reader may read,
/// whatever the filter; <see cref="NextCursor"/> is null on the last page.</summary>
public sealed record SpaceContents(IReadOnlyList<ContentRow> Items, ContentSummary Summary, string? NextCursor);

/// <param name="Path">The titles of the readable ancestors in the tree (a page's own board: of its page), root first, joined by " › "; empty at the root or outside the tree.</param>
/// <param name="Scope">space, wiki or private: an object of the wiki used in the space carries its badge.</param>
/// <param name="HostId">The page a page's own board belongs to (relation attached).</param>
/// <param name="UsedCount">Places it is used that the reader may see, as „Použito na“ counts them (ObjectUsage: links, cards, mentions, reference
/// nodes, pages made from a template; drafts included). Places the reader may not see are not counted: their number is not told.</param>
public sealed record ContentRow(Guid Id, string TypeId, string Title, string Path, string Scope, Guid? SpaceId, Guid? HostId, int UsedCount, int LinksIn,
    int LinksOut, int Comments, DateTime UpdatedAt, bool CanEdit);

/// <param name="Types">Rows of the default listing by type.</param>
public sealed record ContentSummary(IReadOnlyDictionary<string, int> Types, RelationCounts Relations);

public sealed record RelationCounts(int Unused, int NoIncoming, int Broken, int Attached);

/// <summary>An object's neighbourhood (spec §13.3): an end the reader may not read, or one in the trash or gone, is a node with
/// <c>readable=false</c> and nothing else about it.</summary>
/// <param name="Hidden">The object's own edges whose other end the reader may not see (a lock among the nodes, or a draft's use they may not read).</param>
public sealed record ObjectRelations(IReadOnlyList<RelationNodeDto> Nodes, IReadOnlyList<RelationEdgeDto> Edges, int Hidden);

public sealed record RelationNodeDto(Guid Id, string? TypeId, string? Title, Guid? SpaceId, string? SpaceName, bool Readable);

/// <param name="Draft">Only the source page's working revision holds it (asked with drafts).</param>
/// <param name="PartId">Why, for a use of the object asked with reasons: the card, the reference node or the page's block (UsageRow).</param>
/// <param name="Excerpt">The text of the page's block around the use.</param>
/// <param name="Card">A mention that is a contact card (vizitka) rather than a name in the text.</param>
public sealed record RelationEdgeDto(Guid SourceId, Guid TargetId, string Kind, int Count, string? Anchor, bool Draft = false, string? PartId = null, string? Excerpt = null, bool Card = false);

/// <summary>„Použito na“ (plan 3.1 M5): the places an object is used in that the reader may see, a page of them, sorted by space, place
/// and kind; <see cref="Total"/> counts them. Uses in places the reader may not see are neither listed nor counted.</summary>
public sealed record ObjectUsageList(Guid ObjectId, IReadOnlyList<UsageRow> Items, int Total, string? NextCursor);

/// <summary>One use: kde (the place, its space), jak (<see cref="Kind"/>), proč (<see cref="PartId"/>, <see cref="Excerpt"/>) and whether
/// only the working draft holds it.</summary>
/// <param name="PlaceId">The page or board it is used on; for a reference node or an attached board, the page they belong to.</param>
/// <param name="Kind">link, mention, placement, attached or reference (spec 2026-10-27 §13.2).</param>
/// <param name="Count">Cards on the board, reference nodes under the page.</param>
/// <param name="Anchor">The page's section the cards show.</param>
/// <param name="PartId">The first card on the board, the reference node, or the id of the page's block when it has one.</param>
/// <param name="Excerpt">A short plain text of the page's block around the link or mention.</param>
/// <param name="Card">The mention is a contact card (vizitka); its excerpt is that person's card.</param>
public sealed record UsageRow(Guid PlaceId, string PlaceTypeId, string PlaceTitle, Guid? SpaceId, string? SpaceName, string Kind, int Count, string? Anchor,
    bool Draft, string? PartId, string? Excerpt, bool Card = false);

/// <summary>The cheap count of badges and of the last-usage warning: <see cref="Total"/> the uses the reader may see; <see cref="Hidden"/>
/// only whether any use is in a place they may not see (never how many), so the warning does not call a still used object unused.</summary>
public sealed record UsageCount(Guid ObjectId, int Total, bool Hidden);

/// <summary>The space's inventory (plan 3.1 M5): its components by type with their uses, a page of them; <see cref="Types"/> and
/// <see cref="Unused"/> count the whole inventory whatever the filter.</summary>
public sealed record SpaceUsage(IReadOnlyList<UsageInventoryRow> Items, IReadOnlyDictionary<string, int> Types, int Unused, string? NextCursor);

/// <param name="Uses">Uses the reader may see; a wiki's object counts its uses in this space only.</param>
/// <param name="Unused">Not in the tree and used nowhere, hidden places included: what the orphan cleanup may take once it is old enough.</param>
public sealed record UsageInventoryRow(Guid Id, string TypeId, string Title, string Scope, int Uses, bool Unused);
