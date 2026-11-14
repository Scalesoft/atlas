namespace Atlas.Server.Platform.Resources;

/// <summary>
/// The envelope of an object (Atlas 3 F1 plan §2.1): its identity, type, scope, version and lifecycle. Pages (Id = Page.Id) and
/// boards (Id = Board.Id) are space objects whose envelope is written with their rows (ContentRowEffects; the database triggers
/// of migrations 2026102600 and 2026102700 until 2026110930), so no page or board handler writes it; objects of the types the platform stores (IObjectChangeHandler) are written by the
/// object path, with their <see cref="Data"/>. Modules decide access to their own types.
/// </summary>
public class ContentResource
{
    public virtual Guid Id { get; set; }
    public virtual string TypeId { get; set; } = "";
    /// <summary><see cref="ObjectScopes"/>: wiki, space or private.</summary>
    public virtual string Scope { get; set; } = ObjectScopes.Space;
    /// <summary>The space of a space object, null otherwise.</summary>
    public virtual Guid? SpaceId { get; set; }
    /// <summary>The owner of a private object.</summary>
    public virtual Guid? OwnerId { get; set; }
    /// <summary>Raised by every confirmed change.</summary>
    public virtual long Version { get; set; }
    public virtual DateTime CreatedAt { get; set; }
    public virtual Guid? CreatedBy { get; set; }
    public virtual DateTime UpdatedAt { get; set; }
    public virtual DateTime? TrashedAt { get; set; }
    /// <summary>Who moved an object the platform stores to the trash (null for pages and boards, which keep their own, and for an
    /// object trashed before 2026102910).</summary>
    public virtual Guid? TrashedById { get; set; }
    /// <summary>The plain-text title of an object the platform stores, for the library; null for pages and boards.</summary>
    public virtual string? Title { get; set; }
    /// <summary>The JSON object of an object the platform stores; null for pages and boards.</summary>
    public virtual string? Data { get; set; }
}
