using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.SpaceCovers.Domain;

/// <summary>Reads and advances the temporal SpaceCover rows. The caller holds the space FOR UPDATE, so one change at a time.</summary>
public static class SpaceCoverHistory
{
    /// <summary>The current row, which may say Auto; null (Auto as well) when nobody ever changed the image of the space.</summary>
    public static Task<SpaceCover?> CurrentAsync(ISession session, Guid spaceId, CancellationToken ct)
    {
        return session.QueryOver<SpaceCover>().Where(x => x.SpaceId == spaceId && x.ActiveTo == null).SingleOrDefaultAsync<SpaceCover?>(ct);
    }

    /// <summary>The one store method for a choice without a file (the handlers and the restore use it). The same choice again
    /// writes nothing; no row and Auto are the same choice. Returns the file a replaced upload leaves behind, for the caller to
    /// delete after the commit.</summary>
    public static async Task<Guid?> ChooseAsync(ISession session, Guid spaceId, SpaceCoverSource source, string? catalogKey, Guid? actorId, CancellationToken ct)
    {
        var current = await CurrentAsync(session, spaceId, ct);
        if (current is null ? source == SpaceCoverSource.Auto : current.Source == source && current.CatalogKey == catalogKey)
        {
            return null;
        }
        await AdvanceAsync(session, current, new SpaceCover { Id = Guid.NewGuid(), SpaceId = spaceId, Source = source, CatalogKey = catalogKey, ChangedBy = actorId }, ct);
        return current?.FileId;
    }

    /// <summary>Closes the current row and saves <paramref name="next"/> as its successor from the same boundary.</summary>
    public static async Task AdvanceAsync(ISession session, SpaceCover? current, SpaceCover next, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        next.ActiveFrom = current is null ? now : Temporal.NextBoundary(current.ActiveFrom, now);
        if (current is not null)
        {
            current.ActiveTo = next.ActiveFrom;
            // NHibernate flushes inserts before updates: close the current row first, or the new one overlaps it.
            await session.FlushAsync(ct);
        }
        await session.SaveAsync(next, ct);
        await session.FlushAsync(ct);
    }
}
