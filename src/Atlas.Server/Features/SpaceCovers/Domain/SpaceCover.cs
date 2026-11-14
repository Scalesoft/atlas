namespace Atlas.Server.Features.SpaceCovers.Domain;

/// <summary>The image of a space (Knihovna, refresh 2.5), one temporal row per state: an upload, a tint change and a choice
/// close the current row and insert the next one with the actor. Only an Upload has a file (FileId, MimeType, Size and Sha256
/// all set), only a Catalog row has a key; no current row is Auto. Only the current row's file exists in attachment storage.</summary>
public class SpaceCover
{
    public virtual Guid Id { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual SpaceCoverSource Source { get; set; }
    /// <summary>The chosen drawing of <see cref="CoverCatalog"/>; one the running catalog no longer knows reads as Auto.</summary>
    public virtual string? CatalogKey { get; set; }
    public virtual Guid? FileId { get; set; }
    public virtual string? MimeType { get; set; }
    public virtual long? Size { get; set; }
    public virtual string? Sha256 { get; set; }
    /// <summary>The card blends the image into the space's two colours (duotone); false shows it as uploaded.</summary>
    public virtual bool Tinted { get; set; } = true;
    public virtual Guid? ChangedBy { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
