namespace Atlas.Server.Features.Access.Domain;

/// <summary>How much of a card the viewer (the locked request actor) may see: the whole wiki (SpaceId null) or the one space its
/// Admin looks at. Page titles follow the viewer's private draft rule.</summary>
public sealed record AccessScope(UserAccount Viewer, Guid? SpaceId)
{
    public bool Includes(Guid spaceId) => SpaceId is null || SpaceId == spaceId;
}
