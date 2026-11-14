using Atlas.Server.Platform.Objects;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Domain;

/// <summary>
/// The permanent delete of locked trashed pages (unified trash spec §2, §3), for a page with its batch and for a whole space. A page
/// stays as a tombstone (Page.PurgedAt) because immutable access history names it and its revisions (ResourceGrant, PageAccessEvent,
/// PageAccessModeRevision, PageShareLink): every revision keeps only who and when, with its title and body emptied. Gone are the
/// attachments (files after the commit), search and AI projections, links and mentions from it, tags, decorations, views, live
/// editing state, contributions, exports and, as for every object (ObjectPurge), its shares (closed), favorites, visits and comments.
/// The modules' rows (a board, the projector's index) are their purge participants' (IContentPurgeParticipant), the caller's to run.
/// Kept, holding no content: publications (their change summary cleared), the organization history and the Git backup's state
/// (GitBackupPage), so the next Git run commits the deletion of a page it still holds. What each table of a page or space keeps is
/// pinned by PurgeCoverageTests. Statements constant in the number of pages: the tombstones are one UPDATE, not a flush per page.
/// </summary>
public static class PagePurge
{
    private const string EmptyDocument = """{"type":"doc","content":[]}""";

    /// <returns>The attachment files to remove once the transaction commits.</returns>
    public static async Task<IReadOnlyList<Guid>> RunAsync(ISession session, IReadOnlyList<Page> pages, Guid actorId, CancellationToken ct)
    {
        await session.FlushAsync(ct);
        var ids = pages.Select(page => page.Id).ToArray();
        var pageIds = GuidList.Param(ids);
        // Written behind the session (PERF3-2): the loaded pages leave it, so no later flush or read sees them without PurgedAt.
        foreach (var page in pages)
        {
            session.Evict(page);
        }
        var files = await session.CreateSQLQuery($"""SELECT "Id" FROM "Attachment" WHERE "PageId" = {GuidList.Any(":pages")}""")
            .AddScalar("Id", NHibernate.NHibernateUtil.Guid).SetParameter("pages", pageIds).ListAsync<Guid>(ct);
        // The revisions first, then what was derived from them (the search documents among it).
        await session.CreateSQLQuery($"""
            UPDATE "Page" SET "PurgedAt" = now() WHERE "Id" = {GuidList.Any(":pages")};
            UPDATE "PageRevision" SET "Title" = '', "ContentJson" = :empty, "Markdown" = '' WHERE "PageId" = {GuidList.Any(":pages")};
            UPDATE "PagePublication" SET "ChangeSummary" = NULL WHERE "PageId" = {GuidList.Any(":pages")} AND "ChangeSummary" IS NOT NULL;
            DELETE FROM "WorkingSearchDocument" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "SearchDocument" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "PageRevisionAttachment" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "Attachment" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "PageLink" WHERE "SourcePageId" = {GuidList.Any(":pages")};
            DELETE FROM "PageObjectMention" WHERE "SourcePageId" = {GuidList.Any(":pages")};
            DELETE FROM "PageMentionNotice" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "IndexTask" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "KnowledgePage" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "AiDuplicateDismissal" WHERE "PageId" = {GuidList.Any(":pages")} OR "OtherPageId" = {GuidList.Any(":pages")};
            DELETE FROM "PagePdfExport" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "PageTag" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "PageDecoration" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "PageView" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "CollaborationUpdate" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "CollaborationSecretReference" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "PageCollaboration" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "PageContribution" WHERE "PageId" = {GuidList.Any(":pages")};
            DELETE FROM "ContentReference" WHERE "SourceId" = {GuidList.Any(":pages")};
            DELETE FROM "ContentReferenceSource" WHERE "SourceId" = {GuidList.Any(":pages")};
            DELETE FROM "RequirementConversionBackup" WHERE "PageId" = {GuidList.Any(":pages")};
            """).SetParameter("empty", EmptyDocument).SetParameter("pages", pageIds).ExecuteUpdateAsync(ct);
        await ObjectPurge.ForgetAsync(session, ids, actorId, ct);
        return files.ToArray();
    }

    /// <summary>Removes the files of a committed purge. A failure leaves an unreferenced file, never a broken page.</summary>
    public static void RemoveFiles(Features.Attachments.AttachmentFiles files, IEnumerable<Guid> ids, ILogger log)
    {
        foreach (var id in ids)
        {
            try
            {
                files.Remove(id);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(e, "Could not delete the file {FileId} of a purged page", id);
            }
        }
    }
}
