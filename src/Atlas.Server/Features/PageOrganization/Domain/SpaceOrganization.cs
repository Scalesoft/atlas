namespace Atlas.Server.Features.PageOrganization.Domain;

/// <summary>Tree concurrency token, moved with every tree row written (ContentRowEffects and the bare SQL of the tree's types).</summary>
public class SpaceOrganization
{
    public virtual Guid SpaceId { get; set; }
    public virtual long Version { get; set; }
    /// <summary>„Řadit abecedně“ of the space root (<see cref="TitleOrder"/>); a page holds its own children's.</summary>
    public virtual bool SortRootByTitle { get; set; }
}
