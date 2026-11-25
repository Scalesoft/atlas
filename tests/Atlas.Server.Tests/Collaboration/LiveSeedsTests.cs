using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Persistence;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Pages;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Atlas.Server.Tests.Pages.RequirementHeadingConverterTests;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>
/// 3.6 (J1.1): the server seeds every page's live state from its current revision with the managed authority (LiveSeeds), so the bundle carries
/// it and the page editor never seeds. Migration 2026110997 queues the backfill over existing pages; a page still in the pre-3.6
/// requirement block shape is converted on open before its seed, never seeded in the old shape.
/// </summary>
public sealed class LiveSeedsTests
{
    private const string Owner = "00000000-0000-0000-0000-000000000001", Space = "00000000-0000-0000-0000-00000000000a";

    private static string Id(char kind, int n) => $"00000000-0000-0000-0000-{(kind == 'p' ? 'a' : 'b')}{n:D11}";

    private static string Content(string text) =>
        """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Nadpis"}]},"""
        + """{"type":"paragraph","content":[{"type":"text","text":""" + JsonSerializer.Serialize(text) + "}]}]}}";

    private static string Page(int n, int revision, string content, bool trashed = false) => $"""
        INSERT INTO "Page"("Id","SpaceId","OwnerId","Version"{(trashed ? ",\"IsTrashed\",\"TrashedAt\",\"TrashedById\",\"TrashBatchId\"" : "")})
          VALUES('{Id('p', n)}','{Space}','{Owner}',1{(trashed ? $",true,now(),'{Owner}',gen_random_uuid()" : "")});
        INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel")
          VALUES('{Id('r', revision)}','{Id('p', n)}',1,'P','{content.Replace("'", "''")}','','{Owner}',now(),NULL,'web');
        """;

    private const string States = """
        SELECT coalesce(string_agg(p."Id" || ':' || coalesce(c."Generation"::text, '-') || ':' || coalesce(c."StateSha256", '-') || ':' || coalesce(c."BaseRevisionId"::text, '-')
          || ':' || coalesce(c."SchemaBound"::text, '-'), E'\n' ORDER BY p."Id"), '') FROM "Page" p LEFT JOIN "PageCollaboration" c ON c."PageId" = p."Id"
        """;

    [PostgreSqlFact]
    public async Task The_migration_seeds_existing_pages_once_and_a_second_run_changes_nothing()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_live_seeds_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110997);
        // p1 never opened; p2 reset by a write outside a live session (generation without state); p3 in the trash; p4 still in the pre-3.6
        // requirement block shape (seeded only once converted); p5 a live state on an older revision (OpenCollaboration answers Stale: not the seed's to decide);
        // p6 opened by several at once.
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','writer','Writer',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','First','book-open','blue');
            {Page(1, 1, Content("Nikdy neotevřená"))}
            {Page(2, 2, Content("Po zápisu mimo živou relaci"))}
            {Page(3, 3, Content("V koši"), trashed: true)}
            {Page(4, 4, Envelope(Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e601", "jeden", "Jeden požadavek")))}
            {Page(5, 5, Content("Novější verze"))}
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel")
              VALUES('{Id('r', 50)}','{Id('p', 5)}',0,'P','{Content("Starší")}','','{Owner}',now()-interval '1 hour',now()-interval '30 minutes','web');
            {Page(6, 6, Content("Otevřená naráz"))}
            INSERT INTO "PageCollaboration"("PageId","Generation","BaseRevisionId","Sequence","LastSavedSequence","SnapshotSequence","UpdatedAt") VALUES
              ('{Id('p', 2)}','00000000-0000-0000-0000-0000000000c2','{Id('r', 2)}',0,0,0,now()),
              ('{Id('p', 5)}','00000000-0000-0000-0000-0000000000c5','{Id('r', 50)}',0,0,0,now());
            """);
        await AtlasMigrations.MigrateAsync(schema.ConnectionString);
        Assert.Equal(1L, await OutboxTests.ScalarAsync(schema.ConnectionString,
            """SELECT count(*) FROM "JobRun" WHERE "JobId" = 'collaboration.live-seeds' AND "State" = 'queued'"""));
        var before = (string)(await OutboxTests.ScalarAsync(schema.ConnectionString, States))!;

        var authority = new CountingAuthority(Authority());
        await using var host = await AtlasTestHost.CreateAsync(schema, services => services.AddSingleton<ILiveWriteAuthority>(authority));
        // The requirement conversion's own job is not run here: the seed tries repair on open on p4 first, and seeds it only converted.
        await OutboxTests.ExecuteAsync(host.ConnectionString, """DELETE FROM "JobRun" WHERE "JobId" <> 'collaboration.live-seeds'""");
        var seeds = host.Services.GetRequiredService<LiveSeeds>();
        // Four opens at once: one seed, built once.
        Assert.All(await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => seeds.EnsureAsync(Guid.Parse(Id('p', 6)), CancellationToken.None))), Assert.True);
        Assert.Equal(1, authority.Seeds);

        var runner = host.Services.GetRequiredService<IJobRunner>();
        await runner.RunToEndAsync(LiveSeedsJob.JobId, "Test");

        var after = ((string)(await OutboxTests.ScalarAsync(host.ConnectionString, States))!).Split('\n');
        var seeded = authority.Seeds;
        Assert.InRange(seeded, 3, 4);
        foreach (var (page, revision) in new[] { (1, 1), (2, 2), (6, 6) })
        {
            var row = Assert.Single(after, line => line.StartsWith(Id('p', page), StringComparison.Ordinal)).Split(':');
            Assert.NotEqual("-", row[2]);
            Assert.Equal((Id('r', revision), "true"), (row[3], row[4]));
        }
        // p2 keeps its generation (only its state was missing); the others are as they were.
        Assert.Contains($"{Id('p', 2)}:00000000-0000-0000-0000-0000000000c2:", after.Single(line => line.StartsWith(Id('p', 2), StringComparison.Ordinal)));
        foreach (var page in new[] { 3, 5 })
        {
            Assert.Equal(before.Split('\n').Single(line => line.StartsWith(Id('p', page), StringComparison.Ordinal)),
                after.Single(line => line.StartsWith(Id('p', page), StringComparison.Ordinal)));
        }
        // The stored state is the page as the editor holds it, at sequence 0, with the server's projection.
        var stored = (byte[])(await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT "State" FROM "PageCollaboration" WHERE "PageId"='{Id('p', 1)}'"""))!;
        Assert.Contains("Nikdy neotevřená", YjsDocumentMaterializer.Builtin.Materialize(stored));
        Assert.Equal("0:0", await OutboxTests.ScalarAsync(host.ConnectionString,
            $"""SELECT "Sequence" || ':' || "SnapshotSequence" FROM "PageCollaboration" WHERE "PageId"='{Id('p', 1)}' AND strpos("ProjectionJson", 'Nikdy neotevřená') > 0"""));

        // A second run changes nothing seeded; p4 may be seeded now that the conversion's job converted it meanwhile.
        await runner.RunToEndAsync(LiveSeedsJob.JobId, "Test");
        string WithoutP4(string lines) => string.Join('\n', lines.Split('\n').Where(line => !line.StartsWith(Id('p', 4), StringComparison.Ordinal)));
        Assert.Equal(WithoutP4(string.Join('\n', after)), WithoutP4((string)(await OutboxTests.ScalarAsync(host.ConnectionString, States))!));
        Assert.InRange(authority.Seeds, seeded, 4);
        // p4's live state, when it has one, holds requirement headings: the old block never freezes into a live document.
        var p4 = (string?)await OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT "ProjectionJson" FROM "PageCollaboration" WHERE "PageId"='{Id('p', 4)}'""");
        if (p4 is not null)
        {
            Assert.DoesNotContain(RequirementPageConversion.Marker, p4, StringComparison.Ordinal);
        }
    }

    /// <summary>A page stored in the pre-3.6 requirement block shape: the bundle's read converts it (repair on open) before the open seeds it,
    /// so the page and its live state come back as requirement headings.</summary>
    [PostgreSqlFact]
    public async Task An_old_shape_page_opened_through_the_bundle_comes_back_as_headings_with_its_live_state_seeded_from_them()
    {
        await using var f = await McpSite.CreateAsync(services: services => services.AddSingleton<ILiveWriteAuthority>(Authority()));
        // The conversion's jobs (headings, and the systems that take the same key) are not run here: holding the key during the open, they
        // would leave the page as stored (repair on open). Their queued runs go; a run already taken (slow on a loaded host) is waited for,
        // and so is the follow-up run it may request as it ends (ConvertRequirementsHeadings), until neither a queued nor a running one is left.
        var conversions = $"'{RequirementsHeadingsJob.JobId}','{RequirementSystemsJob.JobId}'";
        for (var watch = System.Diagnostics.Stopwatch.StartNew(); ;)
        {
            await f.Site.ExecuteAsync($"""DELETE FROM "JobRun" WHERE "JobId" IN ({conversions}) AND "State"='{JobStates.Queued}'""");
            if ((long)(await f.Site.ScalarAsync($"""SELECT count(*) FROM "JobRun" WHERE "JobId" IN ({conversions}) AND "State" IN ('{JobStates.Running}','{JobStates.Queued}')"""))! == 0)
            {
                break;
            }
            Assert.True(watch.Elapsed < TimeSpan.FromMinutes(1), "A conversion job did not end within a minute.");
            await Task.Delay(50);
        }
        var space = (await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/spaces"), HttpStatusCode.OK))[0]!["id"]!.GetValue<string>();
        var (id, _) = await McpSite.PageAsync(f.Admin, space, "Nasazení", McpSite.Rich(McpSite.Paragraph("Zatím text.")), "Zatím text.", publish: false);
        // Its one announcement's seed lands first (a draft: no publication to announce). Late on a loaded host, that seed's repair on open
        // would hold the conversion's key during the open below, which then serves the page as stored.
        await StoredStateAsync(f, id);
        // As 3.5 stored it: the requirement block, and no live state (the last write was outside a live session), its collaboration row just
        // stamped by a reader's open with nothing unsaved (conversion bug 2 of the 3.6 handoff: that stamp once deferred the repair).
        await f.Site.ScalarAsync("""UPDATE "PageRevision" SET "ContentJson" = @json WHERE "PageId" = @page""",
            ("json", Envelope(Block("6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e601", "jeden", "Jeden požadavek"))), ("page", Guid.Parse(id)));
        await f.Site.ScalarAsync("""
            UPDATE "PageCollaboration" SET "State" = NULL, "StateSha256" = NULL, "ProjectionJson" = NULL,
              "Sequence" = 0, "SnapshotSequence" = 0, "LastSavedSequence" = 0, "UpdatedAt" = now() WHERE "PageId" = @page
            """, ("page", Guid.Parse(id)));

        var opened = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/pages/{id}?bundle=true&visit=true"), HttpStatusCode.OK);

        var content = opened["contentJson"]!.GetValue<string>();
        Assert.DoesNotContain(RequirementPageConversion.Marker, content, StringComparison.Ordinal);
        Assert.Equal(["jeden"], RequirementsRepairOnOpenTests.RequirementHeadingIds(content));
        // On a loaded host the seed may outlast the open's wait (LiveSeeds.OpenWait): it still lands, and the next open carries it.
        var collaboration = opened["bundle"]!["collaboration"] ?? await SeededBundleAsync(f, id);
        Assert.False(collaboration["readOnly"]!.GetValue<bool>());
        // The bundle names the stored seed, which was built from the converted revision: requirement headings, no block.
        Assert.Equal(true, await f.Site.ScalarAsync("""
            SELECT "BaseRevisionId" = (SELECT "Id" FROM "PageRevision" WHERE "PageId" = @page AND "ActiveTo" IS NULL) FROM "PageCollaboration" WHERE "PageId" = @page
            """, ("page", Guid.Parse(id))));
        var projection = (string)(await f.Site.ScalarAsync("""SELECT "ProjectionJson" FROM "PageCollaboration" WHERE "PageId" = @page""", ("page", Guid.Parse(id))))!;
        Assert.DoesNotContain(RequirementPageConversion.Marker, projection, StringComparison.Ordinal);
        Assert.Contains("\"headingId\":\"jeden\"", projection, StringComparison.Ordinal);
        // The state is read beside the bundle (GET …/collaboration/state), gzipped.
        using var gzip = new GZipStream(await f.Admin.GetStreamAsync($"/api/pages/{id}/collaboration/state"), CompressionMode.Decompress);
        using var state = new MemoryStream();
        await gzip.CopyToAsync(state);
        Assert.Equal((byte[])(await f.Site.ScalarAsync("""SELECT "State" FROM "PageCollaboration" WHERE "PageId" = @page""", ("page", Guid.Parse(id))))!, state.ToArray());
    }

    /// <summary>A seed slower than an open waits is not cut off with the open: the open's bundle comes without the live state (the editor
    /// joins the page instead and its join waits for the seed), the seed still lands, and the next open carries it.</summary>
    [PostgreSqlFact]
    public async Task A_seed_slower_than_the_open_waits_still_lands_and_the_next_open_carries_it()
    {
        var authority = new CountingAuthority(Authority());
        await using var f = await McpSite.CreateAsync(services: services => services.AddSingleton<ILiveWriteAuthority>(authority));
        var space = (await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/spaces"), HttpStatusCode.OK))[0]!["id"]!.GetValue<string>();
        var (id, _) = await McpSite.PageAsync(f.Admin, space, "Pomalá", McpSite.Rich(McpSite.Paragraph("Text.")), "Text.", publish: false);
        // Its one announcement (a draft: no publication to announce) seeded it; then its state is gone as after a write outside a live session.
        await StoredStateAsync(f, id);
        await f.Site.ExecuteAsync("""
            UPDATE "PageCollaboration" SET "State" = NULL, "StateSha256" = NULL, "ProjectionJson" = NULL,
              "Sequence" = 0, "SnapshotSequence" = 0, "LastSavedSequence" = 0, "UpdatedAt" = now() WHERE "PageId" = @page
            """, ("page", Guid.Parse(id)));
        authority.Delay = LiveSeeds.OpenWait + TimeSpan.FromSeconds(1);

        var opened = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/pages/{id}?bundle=true&visit=true"), HttpStatusCode.OK);

        Assert.Null(opened["bundle"]!["collaboration"]);
        authority.Delay = TimeSpan.Zero;
        var collaboration = await SeededBundleAsync(f, id);
        Assert.False(collaboration["readOnly"]!.GetValue<bool>());
        Assert.Contains("Text.", YjsDocumentMaterializer.Builtin.Materialize(await StoredStateAsync(f, id)), StringComparison.Ordinal);
    }

    /// <summary>The page's stored live state, once its seed has landed (within 10 s).</summary>
    private static async Task<byte[]> StoredStateAsync(McpSite f, string id)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            if (await f.Site.ScalarAsync("""SELECT "State" FROM "PageCollaboration" WHERE "PageId" = @page""", ("page", Guid.Parse(id))) is byte[] state)
            {
                return state;
            }
            Assert.True(DateTime.UtcNow < deadline, "the live state was not stored within 10 s");
            await Task.Delay(50);
        }
    }

    /// <summary>The bundle's live state of an open after the page's seed has landed.</summary>
    private static async Task<System.Text.Json.Nodes.JsonNode> SeededBundleAsync(McpSite f, string id)
    {
        await StoredStateAsync(f, id);
        return (await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/pages/{id}?bundle=true"), HttpStatusCode.OK))["bundle"]!["collaboration"]!;
    }

    /// <summary>A page whose content the authority refuses is built once: later opens, deliveries and backfills leave it alone (no page
    /// lock, no build) until the page has another revision, which is tried again.</summary>
    [PostgreSqlFact]
    public async Task A_page_the_server_cannot_seed_is_not_tried_again_until_its_revision_changes()
    {
        var authority = new CountingAuthority(Authority()) { Refuse = true };
        await using var f = await McpSite.CreateAsync(services: services => services.AddSingleton<ILiveWriteAuthority>(authority));
        var space = (await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/spaces"), HttpStatusCode.OK))[0]!["id"]!.GetValue<string>();
        var (id, _) = await McpSite.PageAsync(f.Admin, space, "Odmítnutá", McpSite.Rich(McpSite.Paragraph("Text.")), "Text.");
        var seeds = f.Site.Services.GetRequiredService<LiveSeeds>();
        // The page's announcement tried it once (the outbox); its refusal is recorded right after.
        await CollaborationContentGuardHttpTests.WaitUntilAsync(() => authority.Seeds >= 1);
        await Task.Delay(300);

        Assert.False(await seeds.EnsureAsync(Guid.Parse(id), CancellationToken.None));
        Assert.False(await seeds.EnsureAsync(Guid.Parse(id), CancellationToken.None));
        Assert.Equal(1, authority.Seeds);

        authority.Refuse = false;
        var view = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/pages/{id}"), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/pages/{id}", new
        {
            title = "Odmítnutá", contentJson = McpSite.Rich(McpSite.Paragraph("Nový text.")), markdown = (string?)null, expectedVersion = view["version"]!.GetValue<int>(),
        }), HttpStatusCode.OK);
        Assert.True(await seeds.EnsureAsync(Guid.Parse(id), CancellationToken.None));
        await Task.Delay(300);
        Assert.Equal(2, authority.Seeds);
    }

    private static ManagedLiveWriteAuthority Authority() => new();

    private sealed class CountingAuthority(ILiveWriteAuthority inner) : ILiveWriteAuthority
    {
        private int m_seeds;

        public int Seeds => m_seeds;

        /// <summary>Refuse every seed, as content the editor does not open.</summary>
        public bool Refuse { get; set; }

        public Task<DocSnapshot> CheckpointAsync(MarkdownDocumentSchema schema, byte[] snapshot, IReadOnlyList<byte[]> updates, long sequence, CancellationToken ct)
            => inner.CheckpointAsync(schema, snapshot, updates, sequence, ct);

        public Task<byte[]> WriteAsync(MarkdownDocumentSchema schema, byte[] snapshot, IReadOnlyList<byte[]> updates, JsonElement target, long sequence, CancellationToken ct)
            => inner.WriteAsync(schema, snapshot, updates, target, sequence, ct);

        /// <summary>How long every seed takes first, as on a loaded host.</summary>
        public TimeSpan Delay { get; set; }

        public async Task<DocSnapshot> SeedAsync(MarkdownDocumentSchema schema, JsonElement document, CancellationToken ct)
        {
            Interlocked.Increment(ref m_seeds);
            await Task.Delay(Delay, ct);
            return Refuse ? throw new InvalidDataException("[INVALID_CONTENT] test") : await inner.SeedAsync(schema, document, ct);
        }
    }
}
