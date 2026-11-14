namespace Atlas.Server.Features.TrashCleanup.Domain;

/// <summary>The wiki's rule of the cleanup (unified trash spec §4b): <see cref="Scope"/> <c>space</c> lets each space decide, <c>wiki</c>
/// is one rule for all. One current row; no row means each space decides. The automatic cleanup acts as <see cref="ChangedById"/>.</summary>
public class TrashCleanupSetting
{
    public const string SpaceScope = "space";
    public const string WikiScope = "wiki";

    public virtual Guid Id { get; set; }
    public virtual string Scope { get; set; } = SpaceScope;
    public virtual bool Automatic { get; set; }
    public virtual int Days { get; set; }
    public virtual Guid ChangedById { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}

/// <summary>A space's own rule, read only while the wiki's rule lets spaces decide. One current row per space; no row means manual.</summary>
public class SpaceTrashCleanupSetting
{
    public virtual Guid Id { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual bool Automatic { get; set; }
    public virtual int Days { get; set; }
    public virtual Guid ChangedById { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
