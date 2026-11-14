namespace Atlas.Server.Features.TreeReferences;

/// <summary>A reference node of the space tree (migration 2026102850): a placement of the object <see cref="TargetId"/> (with an
/// <see cref="Anchor"/> inside it, e.g. a heading or a block) under <see cref="ParentPageId"/> or at the space root, among the pages and
/// boards by SortOrder. A link, not a copy: removing the node deletes this row only. Trashed only with its page subtree.</summary>
public class TreeReference
{
    public virtual Guid Id { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual Guid? ParentPageId { get; set; }
    public virtual int SortOrder { get; set; }
    public virtual Guid TargetId { get; set; }
    public virtual string? Anchor { get; set; }
    public virtual Guid CreatedBy { get; set; }
    public virtual DateTime CreatedAt { get; set; }
    public virtual bool IsTrashed { get; set; }
    public virtual DateTime? TrashedAt { get; set; }
    public virtual Guid? TrashedById { get; set; }
    public virtual Guid? TrashBatchId { get; set; }
}
