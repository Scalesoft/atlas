using Atlas.Server.Platform.Events;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Commands;

/// <summary>3.6-I: the automatic publication's own delivery of <see cref="VersionClosed"/>: only a version the pause closed (30 minutes
/// without a save, „po nečinnosti“). A version closed by its publication, a restore, a name, the day or AI is no inactivity, and a
/// publication's own close never comes back here.</summary>
public sealed record AutoPublishOnVersionClosed(VersionClosed Event) : IOutboxDelivery<VersionClosed>
{
    public static bool Takes(VersionClosed message) => message.Reason == VersionCloseReasons.Pause;
}

/// <summary>
/// Publishes the version that closed when the page's „Publikace“ is Automatic or Live, with the checks of a publication by hand and
/// within its rules: the publisher is the person who saved the version's newest revision (the author of an AI change is a person too)
/// and must still hold EditContent on the page, as PublishPageHandler requires; a page that was never published stays private (only its
/// author shares it, by hand); a template is published only by hand; a trashed page and a revision no newer than the publication change
/// nothing. Draft approval (Koncepty ke schválení) is the wiki administrator's review of drafts, not a gate a writer's publication waits
/// for, so it is not bypassed: it only finds fewer drafts. Idempotent: a repeat finds the revision published and does nothing.
/// </summary>
public sealed class AutoPublishHandler(ISession session, IAtlasLocks locks, ResourceGrantSnapshots grants, Outbox outbox,
    ILogger<AutoPublishHandler> logger)
{
    public async Task Handle(AutoPublishOnVersionClosed delivery, CancellationToken ct)
    {
        var closed = delivery.Event;
        if (!AutoPublishOnVersionClosed.Takes(closed))
        {
            return;
        }
        // The lock order of a publication: the shared advisory, the space, then the page row.
        await locks.AcquireContentSharedAsync(ct);
        var spaceId = await session.QueryOver<Page>().Where(x => x.Id == closed.PageId).Select(x => x.SpaceId).SingleOrDefaultAsync<Guid>(ct);
        if (spaceId == Guid.Empty)
        {
            return;
        }
        await locks.LockSpaceSharedAsync(spaceId, ct);
        var page = await session.GetForUpdateAsync<Page>(closed.PageId, ct);
        if (page is not { IsTrashed: false, IsTemplate: false, PublishedRevisionId: { } publishedId } || page.SpaceId != spaceId
            || publishedId == closed.RevisionId
            || await PublicationModes.EffectiveAsync(session, page, ct) is not (PublicationMode.Automatic or PublicationMode.Live))
        {
            return;
        }
        // Projections: a revision row holds the whole page.
        var revision = await session.QueryOver<PageRevision>().Where(x => x.Id == closed.RevisionId && x.PageId == page.Id)
            .Select(x => x.Version, x => x.AuthorId).SingleOrDefaultAsync<object[]>(ct);
        var publishedVersion = await session.QueryOver<PageRevision>().Where(x => x.Id == publishedId).Select(x => x.Version).SingleOrDefaultAsync<int>(ct);
        if (revision is not [int version, Guid authorId] || publishedVersion >= version)
        {
            return;
        }
        var author = await session.GetAsync<UserAccount>(authorId, ct);
        if (author is not { IsActive: true } || !(await grants.AuthorizePageAsync(author, page, AccessOperation.EditContent, ct)).Allowed)
        {
            logger.LogInformation("Automatic publication of page {PageId} skipped: the author of revision {RevisionId} may not publish it.",
                page.Id, closed.RevisionId);
            return;
        }
        await PagePublications.RecordAsync(session, outbox, page, closed.RevisionId, author.Id, null, ContentChannels.System, null, ct);
    }
}
