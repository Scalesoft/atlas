namespace Atlas.Server.Features.Spaces.Domain;

/// <summary>The spaces an account sees in space lists (web and personal API): its space roles, plus (D8) every space in
/// which it has a listed page without a space role, mapped to null.</summary>
public static class ListedSpaces
{
    /// <summary>Null for a WikiAdmin, who sees every space as Admin.</summary>
    public static async Task<IReadOnlyDictionary<Guid, SpaceRole?>?> ReadAsync(SpaceRoles roles, PageListing listing, UserAccount actor, CancellationToken ct)
    {
        if (await roles.IsWikiAdminAsync(actor, ct))
        {
            return null;
        }
        var visible = (await roles.EffectiveAsync(actor, ct)).ToDictionary(x => x.Key, x => (SpaceRole?)x.Value);
        foreach (var spaceId in await listing.SpacesWithListedPagesAsync(actor, ct))
        {
            visible.TryAdd(spaceId, null);
        }
        return visible;
    }

    public static SpaceRole? Role(IReadOnlyDictionary<Guid, SpaceRole?>? visible, Guid spaceId) =>
        visible is null ? SpaceRole.Admin : visible[spaceId];
}
