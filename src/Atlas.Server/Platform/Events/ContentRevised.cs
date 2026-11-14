using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Pages;

namespace Atlas.Server.Platform.Events;

/// <summary>
/// A resource's content changed: an outbox event (<see cref="Outbox"/>), ordered per resource. Every C# path that writes a revision
/// announces it through <see cref="ContentRevisions"/>, in its own transaction (RevisionPublicationCoverageTests keeps the list whole;
/// the database triggers of migrations 2026102922 and 2026102950 did it before migration 2026110420). A page's: <c>working</c> for a new
/// working revision (its id), and <c>published</c> for a publication, trash or restore (the published revision's id, or null). A stored
/// object's (a directory contact, <c>atlas.contact</c>): <c>published</c> without a revision id on a change of its data. A
/// <c>rebuild</c> without a revision id says nothing changed: the entity catalog's full rebuild (EntityCatalogRebuildJob, after a
/// restore or a migration) and an archive import, so a consumer rebuilds silently or ignores it. Bare SQL never announces: it requests
/// the rebuild job. Boards announce nothing yet: no consumer reads them, and their first consumer brings the producer for every
/// transition (batch, trash, restore). Each consumer gets its own delivery (<see cref="IOutboxDelivery{TEvent}"/>) and rebuilds from the
/// resource's current state, idempotently: the entity catalog (EntityCatalogRevision), the content references (ContentReferenceRevision)
/// and the page mention notices (PageMentionRevision).
/// </summary>
public sealed record ContentRevised(Guid ResourceId, string ContentType, string RevisionKind, Guid? RevisionId);

/// <summary>The one way a path that writes a revision announces it, in the current handler's transaction, ordered by the resource.</summary>
public static class ContentRevisions
{
    /// <summary>A page's new working revision.</summary>
    public static void PageRevised(this Outbox outbox, Guid pageId, Guid revisionId)
        => outbox.Revised(new(pageId, PagesAtlasModule.ContentType, "working", revisionId));

    /// <summary>A page's publication, trash or restore from the trash: its published revision, null when it has none.</summary>
    public static void PagePublished(this Outbox outbox, Guid pageId, Guid? publishedRevisionId)
        => outbox.Revised(new(pageId, PagesAtlasModule.ContentType, "published", publishedRevisionId));

    /// <summary>A stored object's data changed (created, changed, merged). Only a directory contact is announced: the one stored type a
    /// consumer reads (the entity catalog); another type's first consumer brings its announcement, as for boards.</summary>
    public static void ObjectRevised(this Outbox outbox, Guid objectId, string typeId)
    {
        if (typeId == ContactObjectType.Id)
        {
            outbox.Revised(new(objectId, typeId, "published", null));
        }
    }

    /// <summary>Nothing new to tell: the consumers rebuild the resource from its current state and tell nobody.</summary>
    public static void ContentRebuilt(this Outbox outbox, Guid resourceId, string contentType) => outbox.Revised(new(resourceId, contentType, "rebuild", null));

    private static void Revised(this Outbox outbox, ContentRevised revised) => outbox.Enqueue(revised, revised.ResourceId.ToString());
}
