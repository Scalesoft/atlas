using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.ContentImport;
using Atlas.Server.Features.ContentImport.Commands;
using Atlas.Server.Features.ContentImport.Contracts;
using Atlas.Server.Features.ContentImport.Domain;
using Atlas.Server.Features.ContentImport.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Tests.Platform;
using Xunit;
using Atlas.Server.Features.PageTags.Queries;

namespace Atlas.Server.Tests.ContentImport;

public sealed class ContentImportHandlerTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Preview_is_non_destructive_and_confirmed_import_is_quarantined_with_hierarchy_and_links()
    {
        await using var f = await ContentImportFixture.CreateAsync();
        using var zip = ContentImportTests.Zip(("en/start.md", "---\ntitle: Start\npublished: true\n---\n\n[Next](nested/end.md)"), ("en/nested/end.md", "# End"), ("old.html", "<b>Keep me</b>"), ("notes.txt", "plain"));
        var original = zip.ToArray();
        // The tests of this class run one at a time on their host (ContentImportFixture): a whole table changes only by this test.
        var before = await CountsAsync(f);
        var preview = await f.PreviewAsync(f.Admin, zip, new WikiImportOptions(f.Source, "Imported"));
        Assert.True(preview.CanCommit);
        // A page written in the visual editor converts like a Markdown one; only a file with no page form stays preserved.
        Assert.Equal(5, preview.Counts.Create);
        Assert.Equal(1, preview.Counts.Unsupported);
        Assert.Equal("page", preview.Items.Single(x => x.Path == "old.md").Kind);
        Assert.DoesNotContain(preview.Items, x => x.Path == "old.html");
        Assert.Equal(before, await CountsAsync(f));
        await Denied(400, () => f.As<WikiImportReport>(f.Admin, new CommitImport(preview.Id, new ConfirmWikiImport(preview.ReviewToken, false, true))));
        var result = await f.As<WikiImportReport>(f.Admin, new CommitImport(preview.Id, new ConfirmWikiImport(preview.ReviewToken, true, true)));
        Assert.NotNull(result.CompletedAt);
        Assert.NotNull(result.SpaceId);
        using (var s = f.Host.OpenSession())
        {
            var spaceId = result.SpaceId!.Value;
            var pages = await s.QueryOver<Page>().Where(x => x.SpaceId == spaceId).ListAsync();
            Assert.Equal(5, pages.Count);
            Assert.All(pages, p => Assert.Null(p.PublishedRevisionId));
            Assert.Equal(0, await s.QueryOver<SpaceGrant>().Where(x => x.SpaceId == spaceId).RowCountAsync());
            var end = result.Items.Single(x => x.Path == "en/nested/end.md").PageId!.Value;
            var start = result.Items.Single(x => x.Path == "en/start.md").PageId!.Value;
            var startRevision = await s.QueryOver<PageRevision>().Where(x => x.PageId == start).SingleOrDefaultAsync();
            Assert.Contains(end.ToString(), startRevision.ContentJson);
            var parent = result.Items.Single(x => x.Path == "en/nested.md").PageId!.Value;
            Assert.Equal(parent, pages.Single(x => x.Id == end).ParentId);
            // The import's rows: 5 pages and revisions, no search document (nothing published), 5 working ones, its space, no account.
            Assert.Equal(before with { Page = before.Page + 5, PageRevision = before.PageRevision + 5, WorkingSearchDocument = before.WorkingSearchDocument + 5, Space = before.Space + 1 },
                await CountsAsync(f));
            // T2: the import is its own channel, whoever runs it, and never carries a token.
            Assert.Equal(5L, await s.CreateSQLQuery("""SELECT count(*) FROM "PageRevision" r JOIN "Page" p ON p."Id"=r."PageId" WHERE p."SpaceId"=:space AND r."Channel"='import' AND r."ApiTokenId" IS NULL""")
                .SetParameter("space", spaceId).UniqueResultAsync<long>());
            // The import refreshes the planner's statistics of what it wrote: each estimate is the table's row count.
            foreach (var table in new[] { "Page", "PageRevision", "SearchDocument", "WorkingSearchDocument", "Space", "UserAccount" })
            {
                Assert.Equal(await s.CreateSQLQuery($"SELECT count(*) FROM \"{table}\"").UniqueResultAsync<long>(), await s.RelationRowsAsync(table));
            }
        }
        var repeated = await f.As<WikiImportReport>(f.Admin, new CommitImport(preview.Id, new ConfirmWikiImport(preview.ReviewToken, true, true)));
        Assert.Equal(result.CompletedAt, repeated.CompletedAt);
        await using var source = await SourceAsync(f, f.Admin, preview.Id);
        using var bytes = new MemoryStream();
        await source.CopyToAsync(bytes);
        Assert.Equal(original, bytes.ToArray());
    }

    [PostgreSqlFact]
    public async Task Reimport_is_idempotent_and_explicit_updates_create_temporal_revisions_without_overwriting_edits()
    {
        await using var f = await ContentImportFixture.CreateAsync();
        var first = await f.Import("# First");
        var id = first.Items.Single().PageId!.Value;
        var again = await f.Import("# First");
        Assert.Equal(1, again.Counts.Skip);
        Assert.Equal(id, again.Items.Single().PageId);
        var changed = await f.Import("# Second", true);
        Assert.Equal(1, changed.Counts.Update);
        using (var s = f.Host.OpenSession())
        {
            var revisions = await s.QueryOver<PageRevision>().Where(x => x.PageId == id).OrderBy(x => x.Version).Asc.ListAsync();
            Assert.Equal(2, revisions.Count);
            Assert.Equal(revisions[1].ActiveFrom, revisions[0].ActiveTo);
            Assert.Null(revisions[1].ActiveTo);
            Assert.All(revisions, r => Assert.Equal(DateTimeKind.Utc, r.ActiveFrom.Kind));
            Assert.Equal(2L, await s.CreateSQLQuery("""SELECT count(*) FROM "WikiImportCheckpoint" c JOIN "WikiImportPage" p ON p."Id"=c."ImportPageId" WHERE p."PageId"=:id""")
                .SetParameter("id", id).UniqueResultAsync<long>());
        }
        var page = await f.As<PageDto>(f.Admin, new GetPage(id));
        await f.As<PageDto>(f.Admin, new SavePage(id, new SavePageRequest("User edit", page.ContentJson, page.Markdown, 2)));
        using var zip = ContentImportTests.Zip(("article.md", "# Third"));
        var conflict = await f.PreviewAsync(f.Admin, zip, new WikiImportOptions(f.Source, "Import", UpdateExisting: true));
        Assert.False(conflict.CanCommit);
        Assert.Equal(1, conflict.Counts.Conflict);
        await Denied(409, () => f.As<WikiImportReport>(f.Admin, new CommitImport(conflict.Id, new ConfirmWikiImport(conflict.ReviewToken, true, true))));
        Assert.Equal("User edit", (await f.As<PageDto>(f.Admin, new GetPage(id))).Title);
    }

    [PostgreSqlFact]
    public async Task Revoked_admin_and_new_shared_grants_are_rechecked_at_commit()
    {
        await using var f = await ContentImportFixture.CreateAsync();
        using var zip = ContentImportTests.Zip(("a.md", "A"));
        var preview = await f.PreviewAsync(f.Admin, zip, new WikiImportOptions(f.Source, "Import"));
        await f.Execute("UPDATE \"UserAccount\" SET \"IsWikiAdmin\"=false WHERE \"Id\"=:id", f.Admin.Id);
        await Denied(403, () => f.As<WikiImportReport>(f.Admin, new CommitImport(preview.Id, new ConfirmWikiImport(preview.ReviewToken, true, true))));
        await Denied(403, () => f.As<WikiImportReport>(f.Admin, new GetImportReport(preview.Id)));
        await f.Execute("UPDATE \"UserAccount\" SET \"IsWikiAdmin\"=true WHERE \"Id\"=:id", f.Admin.Id);
        var result = await f.As<WikiImportReport>(f.Admin, new CommitImport(preview.Id, new ConfirmWikiImport(preview.ReviewToken, true, true)));
        await f.Host.SeedAsync(async session => await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = result.SpaceId!.Value, UserId = f.Other.Id, Role = 1 }));
        using var next = ContentImportTests.Zip(("a.md", "New"));
        await Denied(409, () => f.PreviewAsync(f.Admin, next, new WikiImportOptions(f.Source, "Import", UpdateExisting: true)));
        await Denied(403, () => f.As<WikiImportReport>(f.Other, new GetImportReport(preview.Id)));
    }

    [PostgreSqlFact]
    public async Task Asset_references_are_page_owned_retained_and_changed_assets_affect_idempotence()
    {
        await using var f = await ContentImportFixture.CreateAsync();
        // The tests of this class run one at a time on their host (ContentImportFixture): a whole table changes only by this test.
        (int Attachments, int Revisions, int References) before;
        using (var s = f.Host.OpenSession())
        {
            before = (await s.QueryOver<Attachment>().RowCountAsync(), await s.QueryOver<PageRevision>().RowCountAsync(),
                await s.QueryOver<PageRevisionAttachment>().RowCountAsync());
        }
        var first = await f.ImportWithFile("first attachment");
        Assert.Equal(1, first.Counts.Assets);
        var pageId = first.Items.Single(x => x.Kind == "page").PageId!.Value;
        using (var s = f.Host.OpenSession())
        {
            // The import's only attachment, revision and reference are the page's.
            var attachment = await s.QueryOver<Attachment>().Where(x => x.PageId == pageId).SingleOrDefaultAsync();
            Assert.Equal(before.Attachments + 1, await s.QueryOver<Attachment>().RowCountAsync());
            var revision = await s.QueryOver<PageRevision>().Where(x => x.PageId == pageId).SingleOrDefaultAsync();
            Assert.Equal(before.Revisions + 1, await s.QueryOver<PageRevision>().RowCountAsync());
            Assert.Contains(attachment.Id, AttachmentReferences.Extract(revision.ContentJson));
            Assert.Contains(attachment.Id.ToString(), revision.Markdown);
            var refs = await s.QueryOver<PageRevisionAttachment>().RowCountAsync();
            Assert.Equal(before.References + 1, refs);
            Assert.Equal(1, await s.QueryOver<PageRevisionAttachment>().Where(x => x.PageId == pageId).RowCountAsync());
        }
        var update = await f.ImportWithFile("changed attachment", true);
        Assert.Equal(1, update.Counts.Update);
        using (var s = f.Host.OpenSession())
        {
            Assert.Equal(before.Attachments + 2, await s.QueryOver<Attachment>().RowCountAsync());
            Assert.Equal(before.Revisions + 2, await s.QueryOver<PageRevision>().RowCountAsync());
            Assert.Equal(2, await s.QueryOver<PageRevision>().Where(x => x.PageId == pageId).RowCountAsync());
        }
    }

    [PostgreSqlFact]
    public async Task Changes_after_preview_cannot_be_overwritten_and_expired_confirmation_is_rejected()
    {
        await using var f = await ContentImportFixture.CreateAsync();
        var first = await f.Import("First");
        var id = first.Items.Single().PageId!.Value;
        using var zip = ContentImportTests.Zip(("article.md", "Second"));
        var preview = await f.PreviewAsync(f.Admin, zip, new WikiImportOptions(f.Source, "Import", UpdateExisting: true));
        var page = await f.As<PageDto>(f.Admin, new GetPage(id));
        await f.As<PageDto>(f.Admin, new SavePage(id, new SavePageRequest("Changed", page.ContentJson, page.Markdown, 1)));
        await Denied(409, () => f.As<WikiImportReport>(f.Admin, new CommitImport(preview.Id, new ConfirmWikiImport(preview.ReviewToken, true, true))));
        using var other = ContentImportTests.Zip(("x.md", "New"));
        var expired = await f.PreviewAsync(f.Admin, other, new WikiImportOptions(f.Source + "-other", "Other"));
        await f.Execute("UPDATE \"WikiImportBatch\" SET \"ExpiresAt\"=now()-interval '1 hour' WHERE \"Id\"=:id", expired.Id);
        await Denied(410, () => f.As<WikiImportReport>(f.Admin, new CommitImport(expired.Id, new ConfirmWikiImport(expired.ReviewToken, true, true))));
    }

    [PostgreSqlFact]
    public async Task Unsaved_collaboration_updates_before_and_after_preview_are_never_discarded()
    {
        await using var f = await ContentImportFixture.CreateAsync();
        var first = await f.Import("Initial");
        var id = first.Items.Single().PageId!.Value;
        var store = f.Collaboration;
        var opened = await store.OpenAsync(id, f.Admin);
        using var zip = ContentImportTests.Zip(("article.md", "Imported update"));
        var preview = await f.PreviewAsync(f.Admin, zip, new WikiImportOptions(f.Source, "Import", UpdateExisting: true));
        Assert.True(preview.CanCommit);
        await store.AppendUpdateAsync(opened.DocumentId, Guid.NewGuid(), f.Admin.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 7, 8, 9 }, default);
        await Denied(409, () => f.As<WikiImportReport>(f.Admin, new CommitImport(preview.Id, new ConfirmWikiImport(preview.ReviewToken, true, true))));
        var retained = (await store.LoadAsync(opened.DocumentId))!;
        Assert.Equal(new byte[] { 7, 8, 9 }, Assert.Single(retained.Updates).Update);
        Assert.Equal(opened.Generation, (await store.OpenAsync(id, f.Admin)).Generation);
        using var next = ContentImportTests.Zip(("article.md", "Imported update"));
        var conflict = await f.PreviewAsync(f.Admin, next, new WikiImportOptions(f.Source, "Import", UpdateExisting: true));
        Assert.False(conflict.CanCommit);
        Assert.Equal(1, conflict.Counts.Conflict);
    }

    [PostgreSqlFact]
    public async Task Database_failure_rolls_back_all_pages_and_cleans_only_new_attachment_files()
    {
        // Own host: the test creates a trigger on "PageRevision" (DDL) that fails every insert of the database.
        await using var f = await ContentImportFixture.CreateAsync(own: true);
        using var zip = ContentImportTests.Zip(("a.md", "[File](manual.txt)"), ("manual.txt", "Original attachment"));
        var preview = await f.PreviewAsync(f.Admin, zip, new WikiImportOptions(f.Source, "Import"));
        using (var session = f.Host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await session.CreateSQLQuery("CREATE FUNCTION fail_import_revision() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Simulated write failure'; END $$").ExecuteUpdateAsync();
            await session.CreateSQLQuery("CREATE TRIGGER fail_import_revision BEFORE INSERT ON \"PageRevision\" FOR EACH ROW EXECUTE FUNCTION fail_import_revision()").ExecuteUpdateAsync();
            await transaction.CommitAsync();
        }
        await Assert.ThrowsAnyAsync<Exception>(() => f.As<WikiImportReport>(f.Admin, new CommitImport(preview.Id, new ConfirmWikiImport(preview.ReviewToken, true, true))));
        using (var session = f.Host.OpenSession())
        {
            Assert.Equal(0, await session.QueryOver<Page>().RowCountAsync());
            Assert.Equal(0, await session.QueryOver<Space>().RowCountAsync());
            Assert.Equal(0, await session.QueryOver<Attachment>().RowCountAsync());
            Assert.Equal(0, await session.QueryOver<WikiImportPage>().RowCountAsync());
        }
        Assert.Empty(Directory.GetFiles(f.AttachmentDirectory));
        await using (var original = await SourceAsync(f, f.Admin, preview.Id))
        {
            Assert.True(original.Length > 0);
        }
        // The same batch can still be confirmed after the known failure is corrected.
        using (var session = f.Host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await session.CreateSQLQuery("DROP TRIGGER fail_import_revision ON \"PageRevision\"").ExecuteUpdateAsync();
            await transaction.CommitAsync();
        }
        Assert.NotNull((await f.As<WikiImportReport>(f.Admin, new CommitImport(preview.Id, new ConfirmWikiImport(preview.ReviewToken, true, true)))).CompletedAt);
    }

    /// <summary>Mirrors the /source endpoint's authorization (GetImportReport) without its expiry check, which Task 9's HTTP test covers.</summary>
    [PostgreSqlFact]
    public async Task Source_tags_label_new_pages_and_the_stored_report_keeps_per_page_findings()
    {
        await using var f = await ContentImportFixture.CreateAsync();
        using var zip = ContentImportTests.Zip(("a.md", "---\ntitle: A\ntags: ops, Incidents, ops\n---\n\n# A\n\n##### Deep\n\n[[Missing]]"),
            ("b.md", "---\ntitle: B\ntags: [OPS, zeta]\n---\n\nB"));
        var preview = await f.PreviewAsync(f.Admin, zip, new WikiImportOptions(f.Source, "Import"));
        var item = preview.Items.Single(x => x.Path == "a.md");
        Assert.Equal(["Incidents", "ops"], item.Tags);
        Assert.Equal([("wiki-link", 10)], item.Findings!.Select(x => (x.Construct, x.Line ?? 0)));
        await f.As<WikiImportReport>(f.Admin, new CommitImport(preview.Id, new ConfirmWikiImport(preview.ReviewToken, true, true)));
        var stored = (await f.As<WikiImportReport>(f.Admin, new GetImportReport(preview.Id))).Items.Single(x => x.Path == "a.md");
        Assert.Equal(item.Findings!.Select(x => x.Construct), stored.Findings!.Select(x => x.Construct));
        var tags = await f.As<PageTagState>(f.Admin, new GetPageTags(item.PageId!.Value));
        Assert.Equal(["incidents", "ops"], tags.Tags.Select(x => x.Name.ToLowerInvariant()).Order());
        // Pages of one batch share a catalog tag whatever letter case they spelled it in.
        var other = await f.As<PageTagState>(f.Admin, new GetPageTags(preview.Items.Single(x => x.Path == "b.md").PageId!.Value));
        Assert.Equal(tags.Tags.Single(x => x.Name == "ops").Id, other.Tags.Single(x => x.Name.Equals("ops", StringComparison.OrdinalIgnoreCase)).Id);
        Assert.Contains(other.Tags, x => x.Name == "zeta");
    }

    [PostgreSqlFact]
    public async Task Wrapping_folder_of_a_downloaded_repository_is_left_out_unless_earlier_imports_kept_it()
    {
        await using var f = await ContentImportFixture.CreateAsync();
        using var zip = ContentImportTests.Zip(("wikijs-backup/Projects.md", "# Projects"), ("wikijs-backup/Projects/a.md", "[Nahoru](/Projects)"));
        var preview = await f.PreviewAsync(f.Admin, zip, new WikiImportOptions(f.Source, "Import"));
        Assert.Equal(["Projects.md", "Projects/a.md"], preview.Items.Where(x => x.Kind == "page").Select(x => x.Path).Order(StringComparer.Ordinal));
        Assert.Contains(preview.Warnings, x => x.Contains("wikijs-backup/", StringComparison.Ordinal));
        // The commit reads the archive with the options the preview decided on.
        Assert.NotNull((await f.As<WikiImportReport>(f.Admin, new CommitImport(preview.Id, new ConfirmWikiImport(preview.ReviewToken, true, true)))).CompletedAt);

        // A source whose pages were imported with the folder in their paths keeps it, so a re-import finds them.
        using var first = ContentImportTests.Zip(("en/a.md", "A"), ("README.md", "Readme"));
        var imported = await f.PreviewAsync(f.Admin, first, new WikiImportOptions(f.Source + "-legacy", "Legacy"));
        await f.As<WikiImportReport>(f.Admin, new CommitImport(imported.Id, new ConfirmWikiImport(imported.ReviewToken, true, true)));
        using var again = ContentImportTests.Zip(("en/a.md", "A"));
        var repeated = await f.PreviewAsync(f.Admin, again, new WikiImportOptions(f.Source + "-legacy", "Legacy"));
        Assert.Equal("skip", repeated.Items.Single(x => x.Path == "en/a.md").Action);
    }

    private static async Task<Stream> SourceAsync(ContentImportFixture f, UserAccount actor, Guid id)
    {
        await f.As<WikiImportReport>(actor, new GetImportReport(id));
        return f.Host.Services.GetRequiredService<ContentImportFiles>().Open(id);
    }

    private sealed record Counts(long Page, long PageRevision, long SearchDocument, long WorkingSearchDocument, long Space, long UserAccount);

    private static async Task<Counts> CountsAsync(ContentImportFixture f)
    {
        using var s = f.Host.OpenSession();
        async Task<long> CountAsync(string table) => await s.CreateSQLQuery($"SELECT count(*) FROM \"{table}\"").UniqueResultAsync<long>();
        return new(await CountAsync("Page"), await CountAsync("PageRevision"), await CountAsync("SearchDocument"),
            await CountAsync("WorkingSearchDocument"), await CountAsync("Space"), await CountAsync("UserAccount"));
    }

    private static async Task Denied(int status, Func<Task> action)
    {
        Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);
    }
}
