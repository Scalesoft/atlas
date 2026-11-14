namespace Atlas.Server.Features.SpaceAi.Domain;

/// <summary>G5: whether a space takes part in AI (index, meaning search, related pages, suggestions). One current row per
/// space; no row means the default, included. SQL reads it only through AccessSql.SpaceInAi.</summary>
public class SpaceAiSetting
{
    public virtual Guid Id { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual bool Included { get; set; }
    public virtual Guid ChangedById { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
