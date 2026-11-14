using Atlas.Server.Features.Pages;
using Atlas.Server.Platform.Persistence;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Pages;

/// <summary>Migration 2026110830 over existing rows: a current revision with a requirement block queues pages.requirements-headings once,
/// even when the script runs again; history, a purged page and a database without requirement blocks queue nothing.</summary>
public sealed class RequirementsHeadingsMigrationTests
{
    private const long Version = 2026110830;
    private const string Writer = "00000000-0000-0000-0000-000000000001", Space = "00000000-0000-0000-0000-00000000000a",
        Page = "00000000-0000-0000-0000-0000000000a1", Revision = "00000000-0000-0000-0000-0000000000b1";
    private const string Block = """{"type":"requirement","attrs":{"key":"vpn"}}""";

    private static string Seed(string content, bool active, bool purged) => $"""
        INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',false,true,'stamp');
        INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
        INSERT INTO "Page"("Id","SpaceId","OwnerId","Version","IsTrashed","TrashedAt","TrashedById","TrashBatchId","PurgedAt") VALUES ('{Page}','{Space}','{Writer}',1,{(purged ? $"true, now(), '{Writer}', gen_random_uuid(), now()" : "false, NULL, NULL, NULL, NULL")});
        INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel")
          VALUES ('{Revision}','{Page}',1,'Zakázka','{content}','x','{Writer}','2026-10-01T10:00:00Z',{(active ? "NULL" : "'2026-10-02T10:00:00Z'")},'web');
        """;

    private static async Task<object?> QueuedAsync(string connectionString) => await OutboxTests.ScalarAsync(connectionString,
        """SELECT count(*) || ',' || coalesce(min("Reason"), '') FROM "JobRun" WHERE "JobId" = 'pages.requirements-headings' AND "State" = 'queued'""");

    [PostgreSqlTheory]
    [InlineData(true, false, "1,Aktualizace na verzi 3.6")]
    [InlineData(false, false, "0,")]
    [InlineData(true, true, "0,")]
    public async Task Queues_the_conversion_once_only_where_a_current_revision_holds_a_requirement_block(bool active, bool purged, string expected)
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirements_headings_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, Version);
        await OutboxTests.ExecuteAsync(schema.ConnectionString, Seed(Block, active, purged));
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        Assert.Equal(expected, await QueuedAsync(host.ConnectionString));

        // Running the script again (a restore of the same state, a second start) adds nothing.
        var assembly = typeof(AtlasDatabase).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(x => x.EndsWith("2026110830_requirements_headings_job.sql", StringComparison.Ordinal)))!;
        await OutboxTests.ExecuteAsync(host.ConnectionString, await new StreamReader(stream).ReadToEndAsync());
        Assert.Equal(expected, await QueuedAsync(host.ConnectionString));
    }

    [PostgreSqlFact]
    public async Task A_database_without_requirement_blocks_queues_nothing()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_requirements_headings_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, Version);
        await OutboxTests.ExecuteAsync(schema.ConnectionString, Seed("""{"type":"paragraph"}""", true, false));
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        Assert.Equal("0,", await QueuedAsync(host.ConnectionString));
    }
}
