using Atlas.Server.Features.SearchGaps.Contracts;

namespace Atlas.Server.Features.SearchGaps.Queries;

/// <summary>Space admins (and WikiAdmins) only; a member of the space gets 403, anyone else the usual 404 of a space. The list holds
/// queries only, never anything about pages.</summary>
public sealed class GetSearchGapsHandler(IContentAccess access, SearchGapStore store, Atlas.Server.Platform.Retention.RetentionOptions retention)
{
    public async Task<SearchGapList> Handle(GetSearchGaps query, CancellationToken ct)
    {
        await access.LockActorAsync(query.SpaceId, ct);
        await access.RequireSpaceAsync(query.SpaceId, SpaceRole.Admin, ct);
        if (!GetSearchGaps.AllowedDays.Contains(query.Days))
        {
            throw new ApiException(400, "Vyberte období 7, 30 nebo 90 dní.");
        }
        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-query.Days);
        return new SearchGapList(await store.ListAsync(query.SpaceId, since, ct), SearchGapList.Threshold, retention.Effective("SearchQueryLog").Days!.Value);
    }
}
