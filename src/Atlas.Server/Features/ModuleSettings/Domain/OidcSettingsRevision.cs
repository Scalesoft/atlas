namespace Atlas.Server.Features.ModuleSettings.Domain;

/// <summary>One current revision per installation; its Id is the version the UI compares and swaps on.</summary>
public class OidcSettingsRevision
{
    public virtual Guid Id { get; set; }
    public virtual string PublicOrigin { get; set; } = "";
    public virtual Guid AuthorId { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
