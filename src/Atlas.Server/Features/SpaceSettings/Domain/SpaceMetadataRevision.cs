namespace Atlas.Server.Features.SpaceSettings.Domain;

/// <summary>History of Space metadata, written with the space row (ContentRowEffects). Never saved as an entity.</summary>
public class SpaceMetadataRevision
{
    public virtual Guid Id { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual string Name { get; set; } = "";
    public virtual string Icon { get; set; } = "";
    public virtual string Color { get; set; } = "";
    public virtual string Description { get; set; } = "";
    public virtual SpacePinPolicy PinPolicy { get; set; }
    public virtual bool CommentsEnabled { get; set; }
    public virtual Pages.Domain.PublicationMode PublicationMode { get; set; }
    public virtual Guid? ChangedBy { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
