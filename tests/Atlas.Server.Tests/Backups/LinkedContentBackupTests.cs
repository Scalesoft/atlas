using Atlas.Server.Features.Backups.Git;
using Atlas.Server.Features.Backups.Installation;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Contacts.LinkRun.Commands;
using Atlas.Server.Features.Linking.Commands;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.Relations.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Backups;

/// <summary>
/// The owner's key concern for 3.2: a complete ZIP and a Git backup (restored without the key) bring the linked content of 3.2 back into
/// a new installation with the same ids, one contact per person after the background sweeps, the content byte for byte and the graph
/// of what it names (ContentReference) rebuilt to the same rows.
/// </summary>
public sealed class LinkedContentBackupTests
{
    [PostgreSqlFact]
    public async Task Linked_content_comes_back_from_a_complete_zip_and_from_git_without_the_key_with_the_same_ids_one_contact_and_the_same_graph()
    {
        await using var f = await GitBackupTests.GitFixture.CreateAsync();
        var seed = await SeedAsync(f);
        await SettleAsync(f.Pages.Host);
        var before = await StateAsync(f.Pages.Host.ConnectionString, seed, Guid.Empty);
        Assert.Equal(1L, before.Jana);
        Assert.NotEqual("", before.References);
        Assert.Contains($"\"TargetId\": \"{seed.Jana}\"", before.References, StringComparison.Ordinal);
        Assert.Contains("\"State\": \"unresolved\"", before.References, StringComparison.Ordinal);
        // The graph holds the page's chip, the board text's chip and the comment's chip, the plain names and the role place.
        foreach (var part in new[] { "\"SourcePart\": \"rev:", "\"SourcePart\": \"item:", "\"SourcePart\": \"comment:", "\"State\": \"suggested\"" })
        {
            Assert.Contains(part, before.References, StringComparison.Ordinal);
        }
        Assert.NotEqual("", before.Suggestions);
        Assert.Equal("""{"0-7":"PX2"}""", before.YouTrackKeys);

        // (a) A complete ZIP with its key: everything, accounts and the YouTrack configuration too.
        var content = InstallationFixture.Over(f);
        var key = await content.GenerateKeyAsync();
        var zip = await content.ArchiveAsync();
        await using (var target = await InstallationFixture.CreateTargetAsync(f.Root))
        {
            Assert.Equal("full", (await target.RestoreAsync(zip, key.Text)).Mode);
            await SettleAsync(target.Host);
            var after = await StateAsync(target.Host.ConnectionString, seed, target.Restorer.Id);
            Assert.Equal(before, after);
            await AssertUsedAsync(target.Host, seed, seed.Writer);
        }

        // (b) Git, its clone restored without the key: the content alone (accounts come back as inactive names, the secure
        // configuration such as YouTrack stays the target's own).
        await f.Service.GenerateKeyAsync(default);
        await f.Service.SaveSettingsAsync(new GitBackupSettingsRequest(true, f.Url, "main"), default);
        await f.SyncAsync();
        var clone = Path.Combine(f.Root, "clone");
        await f.GitAsync(f.Root, "clone", "--quiet", f.Url, clone);
        await using (var target = await InstallationFixture.CreateTargetAsync(f.Root))
        {
            using (var backup = await BackupDirectory.OpenAsync(clone, null, default))
            {
                Assert.Equal("content-only", (await target.RestoreAsync(backup)).Mode);
            }
            await SettleAsync(target.Host);
            var after = await StateAsync(target.Host.ConnectionString, seed, target.Restorer.Id);
            Assert.Equal(before with { Suggestions = "", YouTrackKeys = null }, after);
            await AssertUsedAsync(target.Host, seed, target.Restorer.Id);
        }
    }

    private sealed record Seed(Guid Writer, Guid Jana, Guid Team, Guid Role, Guid Board, Guid Space);

    /// <summary>What a restore must keep. Contacts of accounts only the target has (its restorer's own) are left out.</summary>
    private sealed record State(long Jana, string Contacts, string Pages, string Boards, string Comments, string References, string Suggestions, string? YouTrackKeys);

    private static async Task<Seed> SeedAsync(GitBackupTests.GitFixture f)
    {
        var pages = f.Pages;
        var jana = Guid.NewGuid();
        var chip = $$$$"""{"type":"contactInline","attrs":{"data":{"version":1,"contacts":[{"name":"Jana Nováková","objectId":"{{{{jana:D}}}}"}]}}}""";
        const string RolePlace = """{"type":"contactInline","attrs":{"data":{"version":1,"contacts":[{"name":"PM","role":"PM","unresolved":true}]}}}""";
        static string Doc(params string[] paragraphs) => """{"format":"atlas-rich","version":1,"plugins":[{"id":"contacts","version":2}],"document":{"type":"doc","content":["""
            + string.Join(",", paragraphs.Select(x => """{"type":"paragraph","content":[""" + x + "]}")) + "]}}";
        static string Text(string text) => $$"""{"type":"text","text":"{{text}}"}""";

        await InstallationFixture.ExecuteAsync(pages.Host.ConnectionString, """
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","OwnerId","Version","CreatedAt","CreatedBy","UpdatedAt","Title","Data") VALUES
              (@jana, 'atlas.contact', 'wiki', NULL, NULL, 1, now(), @writer, now(), 'Jana Nováková', '{"name": "Jana Nováková", "organization": "Modelsoft"}'),
              -- The reader's account contact (directoryId) and a contact of the other writer's name: the sweep suggests that pair.
              (gen_random_uuid(), 'atlas.contact', 'wiki', NULL, NULL, 1, now(), @writer, now(), 'reader', jsonb_build_object('name', 'reader', 'directoryId', CAST(@reader AS text))),
              (gen_random_uuid(), 'atlas.contact', 'wiki', NULL, NULL, 1, now(), @writer, now(), 'other', '{"name": "other"}');
            -- The project keys with an empty token (cleared, or never entered): an empty protected value once failed the whole restore.
            INSERT INTO "YouTrackConfiguration"("Id","Enabled","BaseUrl","ProtectedToken","SpaceProjectsJson","ProjectKeysJson","ChangedById","ActiveFrom")
              VALUES (gen_random_uuid(), true, 'https://youtrack.example.test', '', jsonb_build_object(CAST(@space AS text), jsonb_build_array('0-7'))::text, '{"0-7":"PX2"}', @writer, now());
            """, ("jana", jana), ("writer", pages.Writer.Id), ("reader", pages.Reader.Id), ("space", pages.SpaceId));

        var team = await PublishedAsync(pages, "Projekt Alfa", Doc(Text("Za klienta mluví ") + "," + chip + "," + Text("."), Text("Volal Jana Nováková kvůli PX2-17.")));
        var role = await PublishedAsync(pages, "Role", Doc(Text("PM: ") + "," + RolePlace));
        var board = Guid.NewGuid();
        await InstallationFixture.ExecuteAsync(pages.Host.ConnectionString, $$"""
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SettingsJson","ParentPageId","SortOrder")
              VALUES (@board, @space, 'Plán', 3, now(), @writer, now(), false, '{}', @team, 2048);
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Text","Color","TargetResourceId","Version","IsDeleted","PropsJson","SearchText") VALUES
              (gen_random_uuid(), @board, 'note', 0, 0, 160, 120, 1, 'Zavolat [Jana Nováková](a-contact:?name=Jana%20Nováková&objectId={{jana:D}})', 'yellow', NULL, 1, false, '{}', 'Zavolat Jana Nováková'),
              (gen_random_uuid(), @board, 'object', 200, 0, 240, 120, 2, NULL, NULL, @jana, 1, false, jsonb_build_object('objectId', CAST(@jana AS text), 'typeId', 'atlas.contact', 'view', 'card'), NULL),
              (gen_random_uuid(), @board, 'links', 500, 0, 340, 320, 3, NULL, NULL, NULL, 1, false, jsonb_build_object('show', 'people', 'objectId', CAST(@team AS text)), NULL);
            /* The board's envelope, which its comment references (bare SQL writes it since 2026110930). */
            {{DerivedRows.Sql}}
            INSERT INTO "Comment"("Id","ObjectId","AuthorId","Body","CreatedAt") VALUES
              (gen_random_uuid(), @team, @writer, 'Ptej se [Jana Nováková](a-contact:?name=Jana%20Nováková&objectId={{jana:D}}).', now()),
              (gen_random_uuid(), @board, @writer, 'Domluví to Jana Nováková.', now());
            """, ("board", board), ("space", pages.SpaceId), ("writer", pages.Writer.Id), ("team", team), ("jana", jana));
        return new Seed(pages.Writer.Id, jana, team, role, board, pages.SpaceId);
    }

    private static async Task<Guid> PublishedAsync(Attachments.AttachmentFixture pages, string title, string content)
    {
        var page = await pages.As<PageDto>(pages.Writer, new CreatePage(new CreatePageRequest(pages.SpaceId, null, title, content, title)));
        await pages.As<PageDto>(pages.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
        return page.Id;
    }

    /// <summary>The background sweeps of a running installation, each until it finds nothing: every account's contact, then the graph.</summary>
    private static async Task SettleAsync(AtlasTestHost host)
    {
        host.Actor.Current = null;
        // The restore queues the rebuilds of the page links and of the mentions (what „Použito na“ reads).
        var runner = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Atlas.Server.Platform.Jobs.IJobRunner>(host.Services);
        await runner.RunToEndAsync(Atlas.Server.Features.PageLinks.PageLinkRebuildJob.JobId, "Obnova zálohy");
        await runner.RunToEndAsync(Atlas.Server.Features.Contacts.PageObjectMentionRebuildJob.JobId, "Obnova zálohy");
        foreach (var sweep in new object[] { new SweepAccountContacts(), new SweepContentReferences() })
        {
            var settled = false;
            for (var i = 0; i < 50 && !settled; i++)
            {
                settled = await host.InvokeAsync<int>(sweep) == 0;
            }
            Assert.True(settled, sweep.GetType().Name + " never settled.");
        }
    }

    private static async Task<State> StateAsync(string connectionString, Seed seed, Guid restorer)
    {
        async Task<string> Rows(string sql) => await InstallationFixture.ScalarAsync(connectionString, sql, ("pages", new[] { seed.Team, seed.Role }),
            ("board", seed.Board), ("restorer", restorer.ToString()), ("jana", seed.Jana)) as string ?? "";
        return new State(
            (long)(await InstallationFixture.ScalarAsync(connectionString, """SELECT count(*) FROM "ContentResource" WHERE "TypeId" = 'atlas.contact' AND "Title" = 'Jana Nováková'"""))!,
            await Rows("""
                SELECT string_agg(CAST(to_jsonb(r) AS text), E'\n' ORDER BY r."Id") FROM "ContentResource" r
                WHERE r."TypeId" = 'atlas.contact' AND coalesce(lower(r."Data"->>'directoryId'), '') <> @restorer
                """),
            await Rows("""
                SELECT string_agg(x, E'\n' ORDER BY x) FROM (SELECT CAST(to_jsonb(p) AS text) x FROM "Page" p WHERE p."Id" = ANY(@pages)
                  UNION ALL SELECT CAST(to_jsonb(r) AS text) FROM "PageRevision" r WHERE r."PageId" = ANY(@pages)
                  UNION ALL SELECT CAST(to_jsonb(u) AS text) FROM "PagePublication" u WHERE u."PageId" = ANY(@pages)) s
                """),
            await Rows("""
                SELECT string_agg(x, E'\n' ORDER BY x) FROM (SELECT CAST(to_jsonb(b) AS text) x FROM "Board" b WHERE b."Id" = @board
                  UNION ALL SELECT CAST(to_jsonb(i) AS text) FROM "BoardItem" i WHERE i."BoardId" = @board) s
                """),
            await Rows("""SELECT string_agg(CAST(to_jsonb(c) AS text), E'\n' ORDER BY c."Id") FROM "Comment" c WHERE c."ObjectId" = ANY(@pages) OR c."ObjectId" = @board"""),
            await Rows("""SELECT string_agg(CAST(to_jsonb(r) AS text), E'\n' ORDER BY r."SourceId", r."SourcePart", r."Ordinal") FROM "ContentReference" r"""),
            await Rows($"""SELECT string_agg(k."AccountId" || ':' || k."ContactId" || ':' || k."ByEmail", E'\n' ORDER BY k."AccountId", k."ContactId") FROM ({AccountContacts.Candidates}) k"""),
            await InstallationFixture.ScalarAsync(connectionString, """SELECT "ProjectKeysJson" FROM "YouTrackConfiguration" WHERE "ActiveTo" IS NULL AND "BaseUrl" = 'https://youtrack.example.test'""") as string);
    }

    /// <summary>„Použito na“ of the contact names the page (its chip) and the board (its card and text), as before the backup.</summary>
    private static async Task AssertUsedAsync(AtlasTestHost host, Seed seed, Guid actor)
    {
        using (var session = host.OpenSession())
        {
            host.Actor.Current = await session.GetAsync<UserAccount>(actor);
        }
        var usage = await host.InvokeAsync<ObjectUsageList>(new GetObjectUsage(seed.Jana));
        Assert.Contains(usage.Items, x => x.PlaceId == seed.Team);
        Assert.Contains(usage.Items, x => x.PlaceId == seed.Board);
    }
}
