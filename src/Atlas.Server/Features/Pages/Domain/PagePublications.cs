using Atlas.Server.Platform.Events;
using Atlas.Server.Features.Search;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Domain;

/// <summary>The one write of a publication, by hand (PublishPageHandler) and automatic (AutoPublishHandler).</summary>
public static class PagePublications
{
    /// <summary>Moves the publication to <paramref name="revisionId"/>, whose title and document are exactly the published revision's (a save
    /// that returned to what readers see, SavePageHandler): readers see no change, so the publisher, summary and channel carry over.</summary>
    public static async Task CarryOverAsync(ISession session, Outbox outbox, Page page, Guid revisionId, Guid actorId, CancellationToken ct)
    {
        var open = await session.QueryOver<PagePublication>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync(ct);
        await RecordAsync(session, outbox, page, revisionId, open?.PublisherId ?? actorId, open?.ChangeSummary, open?.Channel ?? ContentChannels.System,
            open?.ApiTokenId, ct);
    }

    /// <summary>Publishes <paramref name="revisionId"/>: closes the current PagePublication, opens the new one, moves the page's pointer
    /// and announces it (ContentRevised <c>published</c>: mentions, AI index, backups, PDF). The page row must be held for update.</summary>
    public static async Task RecordAsync(ISession session, Outbox outbox, Page page, Guid revisionId, Guid publisherId, string? changeSummary,
        string channel, Guid? apiTokenId, CancellationToken ct)
    {
        var previous = await session.QueryOver<PagePublication>()
            .Where(x => x.PageId == page.Id && x.ActiveTo == null)
            .SingleOrDefaultAsync(ct);
        var time = previous is null ? DateTime.UtcNow : Temporal.NextBoundary(previous.ActiveFrom, DateTime.UtcNow);
        if (previous is not null)
        {
            previous.ActiveTo = time;
            await session.FlushAsync(ct);
        }
        await session.SaveAsync(new PagePublication
        {
            PageId = page.Id,
            RevisionId = revisionId,
            PublisherId = publisherId,
            ChangeSummary = changeSummary,
            ActiveFrom = time,
            Channel = channel,
            ApiTokenId = apiTokenId
        }, ct);
        // Current pointer is a read projection of the temporal publication relationship.
        page.PublishedRevisionId = revisionId;
        outbox.PagePublished(page.Id, revisionId);
        await SearchDocuments.RefreshPublishedAsync(session, [page.Id], ct);
    }
}
