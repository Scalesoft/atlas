namespace Atlas.Server.Features.PageOrganization.Contracts;

public sealed record PageTreeDto(Guid SpaceId, long Version, IReadOnlyList<PageTreeItem> Items)
{
    /// <summary>„Řadit abecedně“ of the space root: the root level is shown by title (Domain.TitleOrder).</summary>
    public bool RootByTitle { get; init; }
}
