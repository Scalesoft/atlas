using Atlas.Server.Features.SearchGaps.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Features.SearchGaps.Commands;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.SearchGaps;

/// <summary>
/// Stores misses without identity (D9), for both ways in: the flusher's committed zero-result misses
/// (<see cref="RecordSearchMissesHandler"/>) and the explicit "Nahlásit chybějící obsah" (plan phase 3 Q5). A miss keeps its space
/// only when its user still reaches that space, by a role or a listed page (PageListing.RequireSpaceAsync, as for the space's
/// tree); otherwise it is global. The user id is hashed at once and never stored.
/// </summary>
public sealed class SearchMissWriter(ISession session, SearchGapStore store, PageListing listing, IDataProtectionProvider protection,
    ILogger<SearchMissWriter> logger)
{
    public async Task WriteAsync(IReadOnlyList<SearchMiss> misses, CancellationToken ct)
    {
        if (misses.Count == 0)
        {
            return;
        }
        var (secret, replaced) = await store.SecretAsync(protection.CreateProtector(RecordSearchMissesHandler.Purpose), ct);
        if (replaced)
        {
            logger.LogWarning("The search query log key could not be unprotected and was replaced; distinct users are counted anew");
        }
        var users = new Dictionary<Guid, UserAccount?>();
        var reach = new Dictionary<(Guid User, Guid Space), bool>();
        var rows = new List<SearchQueryLogRow>();
        foreach (var miss in misses.Distinct())
        {
            if (miss.Normalized.Length is 0 or > 200 || !SearchGapReason.IsKnown(miss.Reason))
            {
                continue;
            }
            if (!users.TryGetValue(miss.UserId, out var user))
            {
                users[miss.UserId] = user = await session.GetAsync<UserAccount>(miss.UserId, ct);
            }
            if (user is not { IsActive: true })
            {
                continue;
            }
            var spaceId = miss.SpaceId;
            if (spaceId is { } space)
            {
                if (!reach.TryGetValue((user.Id, space), out var reaches))
                {
                    reach[(user.Id, space)] = reaches = await ReachesAsync(user, space, ct);
                }
                spaceId = reaches ? space : null;
            }
            var display = miss.Display.Length > 200 ? miss.Display[..200] : miss.Display;
            rows.Add(new SearchQueryLogRow(spaceId, miss.Normalized, display, RecordSearchMissesHandler.UserKey(secret, user.Id, spaceId, miss.Normalized),
                miss.On, miss.Reason));
        }
        await store.InsertAsync(rows, ct);
    }

    private async Task<bool> ReachesAsync(UserAccount user, Guid spaceId, CancellationToken ct)
    {
        try
        {
            await listing.RequireSpaceAsync(user, spaceId, ct);
            return true;
        }
        catch (ApiException exception) when (exception.Status == 404)
        {
            return false;
        }
    }
}
