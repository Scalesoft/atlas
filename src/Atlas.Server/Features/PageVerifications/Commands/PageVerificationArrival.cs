using Atlas.Server.Features.PageVerifications.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageVerifications.Commands;

/// <summary>5.2 (plan §2.3) for pages that come into a space other than by creating them (<see cref="PageVerificationStart"/>): an import,
/// a wiki restore or a move from another space.</summary>
public static class PageVerificationArrival
{
    /// <summary>Each page of a space that verifies its pages and without a verification row starts unverified with
    /// <paramref name="stewardId"/> (the actor, who may write in the space) as its steward; templates and existing rows stay as they are.</summary>
    public static async Task StartAsync(ISession session, IEnumerable<Page> pages, Guid stewardId, CancellationToken ct)
    {
        var candidates = new List<Page>();
        foreach (var page in pages)
        {
            if (!page.IsTemplate && (await session.GetAsync<Space>(page.SpaceId, ct))?.VerificationIntervalMonths is not null)
            {
                candidates.Add(page);
            }
        }
        if (candidates.Count == 0)
        {
            return;
        }
        var existing = (await session.QueryOver<PageVerification>().Where(GuidList.In("PageId", candidates.Select(x => x.Id)))
            .Select(x => x.PageId).ListAsync<Guid>(ct)).ToHashSet();
        foreach (var page in candidates.Where(x => !existing.Contains(x.Id)))
        {
            await session.SaveAsync(new PageVerification { PageId = page.Id, StewardId = stewardId }, ct);
        }
    }
}
