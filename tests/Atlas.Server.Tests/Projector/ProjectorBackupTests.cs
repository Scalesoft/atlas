using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Tests.Backups;
using Xunit;
using static Atlas.Server.Tests.Projector.ProjectorDocuments;

namespace Atlas.Server.Tests.Projector;

/// <summary>The projector part of the installation backup (plan chapter 15): catalog marks travel row for row, the requirement
/// index does not and is rebuilt from the restored page content.</summary>
public sealed class ProjectorBackupTests
{
    [PostgreSqlFact]
    public async Task Catalog_marks_come_back_and_the_requirement_index_is_rebuilt_after_a_restore()
    {
        await using var f = await InstallationFixture.CreateAsync();
        await f.PopulateAsync();
        var page = await f.Pages.As<PageDto>(f.Pages.Writer, new CreatePage(new CreatePageRequest(f.Pages.SpaceId, null, "KBN", Json(Paragraph("x")), null)));
        await f.Pages.Host.SeedAsync(session => session.CreateSQLQuery("""
                UPDATE "PageRevision" SET "ContentJson"=:json WHERE "PageId"=:page;
                INSERT INTO "ProjectorCatalogPage"("PageId","MarkedById","MarkedAt") VALUES (:page,:writer,now());
                """)
            .SetParameter("json", Json(Block("b1", "portal.ssl", "SSL", content: [Block("b2", "portal.dns", "DNS")])))
            .SetParameter("page", page.Id).SetParameter("writer", f.Pages.Writer.Id).ExecuteUpdateAsync());
        var key = await f.GenerateKeyAsync();
        var zip = await f.ArchiveAsync();
        var projector = f.Installation.Catalog.Modules.Single(x => x.ModuleId == "projector");
        var expected = await InstallationFixture.FingerprintsAsync(f.ConnectionString, projector.Tables);
        await using var target = await f.CreateTargetAsync();

        await target.RestoreAsync(zip, key.Text);

        // The populated installation marks its own page too.
        Assert.StartsWith("2:", expected["ProjectorCatalogPage"], StringComparison.Ordinal);
        Assert.Equal(expected, await InstallationFixture.FingerprintsAsync(target.Host.ConnectionString, projector.Tables));
        // The fixture has a requirement page of its own (PER1-6); this one is KBN. The index is kept in code (migration 2026110870): the
        // source's raw update in place is refreshed by hand; the restore rebuilt the target's at once (review 2026-11-05: uses, counts and
        // the last-use warning read it right after a restore), with no read and no job.
        using (var session = f.Pages.Host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await Atlas.Server.Features.Projector.Persistence.ProjectorIndex.RefreshAsync(session, [page.Id], default);
            await transaction.CommitAsync();
        }
        Assert.Equal(0L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, """
            SELECT count(*) FROM "PageRevision" r JOIN "Page" p ON p."Id" = r."PageId" AND p."PurgedAt" IS NULL
            WHERE (r."ActiveTo" IS NULL OR r."Id" = p."PublishedRevisionId")
              AND NOT EXISTS (SELECT 1 FROM "ProjectorIndexedRevision" x WHERE x."RevisionId" = r."Id")
            """));
        var index = $"""SELECT string_agg("BlockId" || ':' || coalesce("ParentBlockId",''), ' ' ORDER BY "Position") FROM "ProjectorRequirementIndex" WHERE "PageId" = '{page.Id}' """;
        Assert.Equal("b1: b2:b1", await InstallationFixture.ScalarAsync(f.ConnectionString, index));
        Assert.Equal("b1: b2:b1", await InstallationFixture.ScalarAsync(target.Host.ConnectionString, index));
    }
}
