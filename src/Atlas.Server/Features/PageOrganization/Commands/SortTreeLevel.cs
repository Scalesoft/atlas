using Atlas.Server.Features.PageOrganization.Contracts;

namespace Atlas.Server.Features.PageOrganization.Commands;

/// <summary>„Řadit abecedně“ on or off for one level of the space tree: the direct children of <see cref="SortTreeLevelRequest.ParentId"/>, or the root.</summary>
public sealed record SortTreeLevel(Guid SpaceId, SortTreeLevelRequest Request);
