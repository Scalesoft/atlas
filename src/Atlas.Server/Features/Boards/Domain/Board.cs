namespace Atlas.Server.Features.Boards.Domain;

/// <summary>A board in a space: a leaf of the space tree under ParentPageId (null: the root), ordered among its siblings, pages
/// included, by SortOrder (BoardContent). Sequence counts the committed batches; a trashed board answers 404 everywhere but the
/// space's trash, and its trash stamp says which batch (its own, or its page subtree's) brings it back.</summary>
// shortcut: no per-board grants, a board inherits the roles of its space; item grants come with the platform's IAccessKind.
public class Board
{
    /// <summary>The page's board (Projector's deployment board, spec 3.5.1 §1.10): who may read the page reads it, who may edit the
    /// page edits it. A page's board of any other purpose stays its editors' only (review S4-3).</summary>
    public const string PagePurpose = "projector.board";

    public virtual Guid Id { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual Guid? ParentPageId { get; set; }
    public virtual int SortOrder { get; set; }
    public virtual string Title { get; set; } = "";
    public virtual long Sequence { get; set; }
    public virtual DateTime CreatedAt { get; set; }
    public virtual Guid CreatedBy { get; set; }
    public virtual DateTime UpdatedAt { get; set; }
    public virtual bool IsTrashed { get; set; }
    public virtual DateTime? TrashedAt { get; set; }
    public virtual Guid? TrashedById { get; set; }
    public virtual Guid? TrashBatchId { get; set; }
    /// <summary>jsonb: the settings the board changed (BoardProps.MergeSettings); BoardProps.WithDefaults shows the rest.</summary>
    public virtual string SettingsJson { get; set; } = BoardProps.Empty;
    /// <summary>What a page's own board is for (its <c>Purpose</c>), null for a board of the space tree. Such a board belongs to
    /// its parent page: it is no node of the tree, and the page decides who reads and edits it (BoardAccess).</summary>
    public virtual string? Purpose { get; set; }
}
