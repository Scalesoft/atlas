using Atlas.Server.Features.Backups.Git;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Queries;
using Atlas.Server.Features.Backups.Installation;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Platform.Backup;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Tests.Attachments;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Backups;

/// <summary>Format v7 over Git (plan 15.3): the real git CLI against a local bare repository, like <see cref="GitBackupTests"/>.</summary>
public sealed class InstallationBackupGitTests
{
    [PostgreSqlFact]
    public async Task The_repository_holds_the_whole_installation_and_its_clone_restores_it_row_for_row()
    {
        await using var f = await GitBackupTests.GitFixture.CreateAsync();
        var page = await f.CreateAsync("Runbook", null);
        await f.Pages.UploadAsync(f.Pages.Writer, page, "notes.txt", new MemoryStream("vpn notes"u8.ToArray()));
        await f.Pages.Host.SeedGroupAsync("Provozáci", f.Pages.Writer);
        var key = await EnableAsync(f);

        var status = await f.Service.SyncAsync(true, default);

        Assert.Null(status!.LastError);
        var files = await f.AllFilesAsync();
        Assert.Contains(BackupManifest.Path, files);
        Assert.Contains(GitBackupLayout.AttributesPath, files);
        Assert.Contains($".atlas/modules/pages/Page/{page:D}.json", files);
        Assert.Contains(files, x => x.StartsWith(".atlas/secure/identity/UserGroup/", StringComparison.Ordinal));
        Assert.Contains(files, x => x.StartsWith(".atlas/blobs/", StringComparison.Ordinal));
        // The readable tree and the 2.x page documents stay as they were.
        Assert.Contains("history/runbook.md", files);
        Assert.Equal("Atlas|atlas@atlas.invalid|chore: snapshot installation", (await f.AllCommitsAsync())[^1]);

        var tables = f.Installation.Catalog.Modules.SelectMany(x => x.Tables).ToList();
        var expected = await InstallationFixture.FingerprintsAsync(f.Pages.Host.ConnectionString, tables);
        var clone = Path.Combine(f.Root, "clone");
        await f.GitAsync(f.Root, "clone", "--quiet", f.Url, clone);
        await using var target = await InstallationFixture.CreateTargetAsync(f.Root);
        using var backup = await BackupDirectory.OpenAsync(clone, key, default);
        var result = await target.RestoreAsync(backup);

        Assert.Equal("full", result.Mode);
        var actual = await InstallationFixture.FingerprintsAsync(target.Host.ConnectionString, tables);
        Assert.Empty(expected.Where(x => actual[x.Key] != x.Value).Select(x => x.Key));
        await target.AssertAccountsHaveTheirContactAsync();
    }

    [PostgreSqlFact]
    public async Task Every_board_object_comment_and_page_reaches_the_repository_and_its_clone_restores_the_content_without_a_key()
    {
        // Owner (2026-10-28): the Git backup is the backup of the content; boards and the other components must come back whole
        // from the repository alone, with no backup key anywhere. The content is the one of the every-column test.
        await using var f = await GitBackupTests.GitFixture.CreateAsync();
        var content = InstallationFixture.Over(f);
        await content.PopulateAsync();
        await f.Service.GenerateKeyAsync(default);
        await f.Service.SaveSettingsAsync(new GitBackupSettingsRequest(true, f.Url, "main"), default);
        await f.SyncAsync();

        var files = await f.AllFilesAsync();
        Assert.Contains($".atlas/modules/boards/Board/{content.BoardId:D}.json", files);
        foreach (var module in InstallationFixture.ContentModules)
        {
            Assert.Contains(files, x => x.StartsWith($".atlas/modules/{module}/", StringComparison.Ordinal));
        }
        Assert.Contains($".atlas/blobs/{InstallationFixture.Sha256Of(InstallationFixture.CoverFile)}", files);

        // An edit on the board alone (no page saved) is committed with the next snapshot.
        var note = (Guid)(await InstallationFixture.ScalarAsync(f.Pages.Host.ConnectionString,
            // The check reads PostgreSQL's write counts first; a backend reports them within seconds of its commit, this one at once.
            "UPDATE \"BoardItem\" SET \"Text\" = 'VPN brána 2', \"Version\" = \"Version\" + 1 WHERE \"BoardId\" = @board AND \"Kind\" = 'note' RETURNING \"Id\"; SELECT pg_stat_force_next_flush()",
            ("board", content.BoardId)))!;
        f.Clock.Advance(GitBackupService.SnapshotInterval + TimeSpan.FromSeconds(1));
        await f.TickAsync();
        Assert.Contains($".atlas/modules/boards/BoardItem/{note:D}.json", (await f.RemoteAsync("show", "--name-only", "--format=", "main")).Split('\n'));

        var tables = f.Installation.Catalog.Modules.SelectMany(x => x.Tables).Where(x => !x.Secure).ToList();
        // Without the secure part an API revision keeps its content and loses its token, so its channel becomes "legacy".
        var expected = await InstallationFixture.FingerprintsAsync(f.Pages.Host.ConnectionString, tables, "ApiTokenId", "Channel");
        var clone = Path.Combine(f.Root, "clone");
        await f.GitAsync(f.Root, "clone", "--quiet", f.Url, clone);
        await using var target = await InstallationFixture.CreateTargetAsync(f.Root);
        using var backup = await BackupDirectory.OpenAsync(clone, null, default);
        var result = await target.RestoreAsync(backup);

        Assert.Equal("content-only", result.Mode);
        var actual = await InstallationFixture.FingerprintsAsync(target.Host.ConnectionString, tables, "ApiTokenId", "Channel");
        Assert.Empty(expected.Where(x => actual[x.Key] != x.Value).Select(x => x.Key));
        Assert.Equal("VPN brána 2", await InstallationFixture.ScalarAsync(target.Host.ConnectionString, "SELECT \"Text\" FROM \"BoardItem\" WHERE \"Id\" = @id", ("id", note)));
        Assert.Equal(InstallationFixture.CoverFile, await File.ReadAllBytesAsync(target.Files.PathFor(content.CoverFileId)));
        foreach (var (id, sha256) in await InstallationBackupTests.AttachmentsAsync(f.Pages.Host.ConnectionString))
        {
            Assert.Equal(sha256, InstallationFixture.Sha256(target.Files.PathFor(id)));
        }
        // The restored wiki works: the restorer opens the page and the board with its items.
        target.Host.Actor.Current = target.Restorer;
        Assert.Equal("Provoz", (await target.Host.InvokeAsync<PageDto>(new GetPage(content.PageId))).Title);
        Assert.Contains((await target.Host.InvokeAsync<BoardDto>(new GetBoard(content.BoardId))).Items, x => x.Text == "VPN brána 2");
        // Atlas 3.4: the deployment page and the board composed under it, in the tree, with its cards, connector and overview.
        Assert.Contains($".atlas/modules/boards/Board/{content.DeploymentBoardId:D}.json", files);
        await content.AssertDeploymentRestoredAsync(target.Host.ConnectionString);
        Assert.Equal(["product", "service"], (await target.Host.InvokeAsync<BoardDto>(new GetBoard(content.DeploymentBoardId))).Items
            .Where(x => x.Kind == "page").Select(x => x.Props.GetProperty("display").GetString()).Order());
        Assert.Contains((await target.Host.InvokeAsync<PageTreeDto>(new GetPageTree(f.Pages.SpaceId))).Items,
            x => x.Id == content.DeploymentBoardId && x.Type == "board" && x.ParentId == content.DeploymentPageId);
        await target.AssertAccountsHaveTheirContactAsync();
    }

    [PostgreSqlFact]
    public async Task A_table_with_a_list_in_a_cell_stays_a_table_in_the_readable_markdown_and_restores_without_a_key()
    {
        // Atlas 3.6: the stored Markdown wrote such a table as loose paragraphs; it is the dialect's a-table now.
        await using var f = await GitBackupTests.GitFixture.CreateAsync();
        const string content = """
            {"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"table","content":[
              {"type":"tableRow","content":[{"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"Fáze"}]}]}]},
              {"type":"tableRow","content":[{"type":"tableCell","content":[{"type":"bulletList","content":[
                {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"a"}]}]}]}]}]}]}]}}
            """;
        var page = await f.Pages.As<PageDto>(f.Pages.Writer, new CreatePage(new CreatePageRequest(f.Pages.SpaceId, null, "Tabulka", content, "")));
        await f.Pages.As<PageDto>(f.Pages.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
        await f.Service.GenerateKeyAsync(default);
        await f.Service.SaveSettingsAsync(new GitBackupSettingsRequest(true, f.Url, "main"), default);
        await f.SyncAsync();

        Assert.EndsWith("---\n\n```a-table\n- cells:\n    - header: true\n      content: Fáze\n- cells:\n    - content: \"- a\"\n```",
            await f.RemoteAsync("show", "main:history/tabulka.md"));
        var clone = Path.Combine(f.Root, "clone");
        await f.GitAsync(f.Root, "clone", "--quiet", f.Url, clone);
        await using var target = await InstallationFixture.CreateTargetAsync(f.Root);
        using var backup = await BackupDirectory.OpenAsync(clone, null, default);
        Assert.Equal("content-only", (await target.RestoreAsync(backup)).Mode);
        const string revision = "SELECT \"ContentJson\" || '|' || \"Markdown\" FROM \"PageRevision\" WHERE \"PageId\" = @id ORDER BY \"Version\" DESC LIMIT 1";
        var restored = (string)(await InstallationFixture.ScalarAsync(target.Host.ConnectionString, revision, ("id", page.Id)))!;
        Assert.Equal(await InstallationFixture.ScalarAsync(f.Pages.Host.ConnectionString, revision, ("id", page.Id)), restored);
        Assert.Contains("\"bulletList\"", restored, StringComparison.Ordinal);
    }

    [PostgreSqlFact]
    public async Task A_33_repository_restores_into_the_34_shape_after_its_data_migrations_and_the_overview_conversion()
    {
        // A Git backup Atlas 3.3 wrote (schema 2026110460): a deployment page with the summary under atlas-requirements 1, its deployment
        // board with a lane frame and a requirement card, and on another board a requirement card with its connector to a lane frame.
        await using var f = await GitBackupTests.GitFixture.CreateAsync();
        var content = InstallationFixture.Over(f);
        await content.PopulateAsync();
        var page = (await f.Pages.As<PageDto>(f.Pages.Writer, new CreatePage(new CreatePageRequest(f.Pages.SpaceId, null, "Zakázka", AttachmentFixture.Rich("x"), null)))).Id;
        Guid deployment = Guid.NewGuid(), card = Guid.NewGuid(), connector = Guid.NewGuid(), frame = Guid.NewGuid();
        await InstallationFixture.ExecuteAsync(f.Pages.Host.ConnectionString, """
            UPDATE "PageRevision" SET "ContentJson" = @shape WHERE "PageId" = @page;
            INSERT INTO "Board"("Id","SpaceId","ParentPageId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder","Purpose")
              SELECT @deployment, "SpaceId", @page, 'Tabule nasazení', 3, now(), "CreatedBy", now(), false, 2048, 'projector.deployment' FROM "Board" WHERE "Id" = @board;
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","TargetResourceId","Version","IsDeleted","PropsJson") VALUES
              (gen_random_uuid(), @deployment, 'frame', 0, 0, 600, 400, 1, NULL, 1, false, '{"lane": "portal"}'),
              (gen_random_uuid(), @deployment, 'page', 20, 40, 280, 128, 2, @page, 1, false, '{"display": "requirement", "anchor": "vpn"}'),
              (@frame, @board, 'frame', 0, 600, 600, 400, 11, NULL, 1, false, '{"lane": "portal"}'),
              (@card, @board, 'page', 20, 640, 280, 128, 12, @page, 1, false, '{"display": "requirement", "anchor": "vpn"}');
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","FromItemId","ToItemId","Version","IsDeleted","PropsJson")
              VALUES (@connector, @board, 'connector', 0, 0, 8, 8, 13, @card, @frame, 1, false, '{"route": "elbow", "arrowStart": "none", "arrowEnd": "arrow", "dash": "solid"}');
            """ + DerivedRows.Sql, ("shape", Shape33), ("page", page), ("deployment", deployment), ("board", content.BoardId), ("card", card), ("connector", connector), ("frame", frame));
        await f.Service.GenerateKeyAsync(default);
        await f.Service.SaveSettingsAsync(new GitBackupSettingsRequest(true, f.Url, "main"), default);
        await f.SyncAsync();
        Assert.Contains($".atlas/modules/boards/Board/{deployment:D}.json", await f.AllFilesAsync());
        var clone = Path.Combine(f.Root, "clone");
        await f.GitAsync(f.Root, "clone", "--quiet", f.Url, clone);
        var manifest = Path.Combine(clone, ".atlas", "manifest.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(manifest))!;
        json["schema"] = 2026110460;
        await File.WriteAllTextAsync(manifest, json.ToJsonString());
        await using var target = await InstallationFixture.CreateTargetAsync(f.Root);
        using var backup = await BackupDirectory.OpenAsync(clone, null, default);

        Assert.Equal("content-only", (await target.RestoreAsync(backup)).Mode);

        // Migration 2026110470 again: the deployment board is gone, the card and its connector are deleted, the frame keeps no lane.
        var db = target.Host.ConnectionString;
        Assert.Equal(0L, await InstallationFixture.ScalarAsync(db, """SELECT count(*) FROM "ContentResource" WHERE "Id" = @id""", ("id", deployment)));
        Assert.Equal("true,true", await InstallationFixture.ScalarAsync(db,
            """SELECT string_agg("IsDeleted"::text, ',' ORDER BY "Z") FROM "BoardItem" WHERE "Id" IN (@card, @connector)""", ("card", card), ("connector", connector)));
        Assert.Equal("{}", await InstallationFixture.ScalarAsync(db, """SELECT "PropsJson"::text FROM "BoardItem" WHERE "Id" = @id""", ("id", frame)));
        // Migration 2026110480 again queues the conversion, which the restart runs: the page ends in the 3.4 shape.
        Assert.Equal(1L, await InstallationFixture.ScalarAsync(db,
            $"""SELECT count(*) FROM "JobRun" WHERE "JobId" = '{RequirementsOverviewJob.JobId}' AND "State" = 'queued'"""));
        await target.Host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(RequirementsOverviewJob.JobId, "Obnova zálohy");
        var converted = System.Text.Json.Nodes.JsonNode.Parse((string)(await InstallationFixture.ScalarAsync(db,
            """SELECT "ContentJson" FROM "PageRevision" WHERE "PageId" = @id AND "ActiveTo" IS NULL""", ("id", page)))!)!;
        // Atlas 3.6: the conversion lifts the plugin to the current version, atlas-requirements 4 (CollaborationSchema).
        Assert.Equal(4, (int)converted["plugins"]![0]!["version"]!);
        Assert.Equal(("overview", Summary, "requirements"), ((string?)converted["document"]!["content"]![0]!["type"],
            (string?)converted["document"]!["content"]![0]!["attrs"]!["id"], (string?)converted["document"]!["content"]![0]!["attrs"]!["kind"]));
        Assert.DoesNotContain("requirementSummary", converted.ToJsonString(), StringComparison.Ordinal);
        // The history line 3.5.0 read as nested ordered lists (migration 2026110550, the same job) is one dated line again.
        Assert.Equal("30. 10. 2026 — telefonát", (string?)converted["document"]!["content"]![1]!["content"]![1]!["content"]![0]!["content"]![0]!["content"]![0]!["text"]);
        // The 3.4 content of the same backup is left as it was by both.
        await content.AssertDeploymentRestoredAsync(db);
        target.Host.Actor.Current = target.Restorer;
        Assert.Equal("Zakázka", (await target.Host.InvokeAsync<PageDto>(new GetPage(page))).Title);
    }

    private const string Summary = "6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e6f0";

    /// <summary>A page as 3.3 stored it: the deployment summary first, then one requirement block (RequirementsOverviewJobTests).</summary>
    private const string Shape33 = $$$"""
        {"format":"atlas-rich","version":1,"plugins":[{"id":"atlas-requirements","version":1}],"document":{"type":"doc","content":[{"type":"requirementSummary","attrs":{"id":"{{{Summary}}}"}},{"type":"requirement","attrs":{"id":"6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e6f8","key":"vpn","direction":"internal","state":"planned","date":"","party":"","where":"","requires":""},"content":[{"type":"paragraph","content":[{"type":"text","text":"VPN"}]},{"type":"bulletList","attrs":{"tight":true},"content":[{"type":"listItem","content":[{"type":"paragraph"},{"type":"orderedList","attrs":{"tight":true,"start":30},"content":[{"type":"listItem","content":[{"type":"paragraph"},{"type":"orderedList","attrs":{"tight":true,"start":10},"content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"2026 — telefonát"}]}]}]}]}]}]}]}]}]}}
        """;

    [PostgreSqlFact]
    public async Task A_snapshot_holds_only_the_changed_rows_and_without_page_changes_waits_for_its_interval()
    {
        await using var f = await GitBackupTests.GitFixture.CreateAsync();
        var page = await f.CreateAsync("Runbook", null);
        var group = await f.Pages.Host.SeedGroupAsync("Provozáci", f.Pages.Writer);
        await EnableAsync(f);
        await f.SyncAsync();

        // A saved revision: its commit by the author, then one snapshot of the rows it changed.
        await f.Pages.As<PageDto>(f.Pages.Writer, new SavePage(page, new SavePageRequest("Runbook", AttachmentFixture.Rich("v2"), "v2", 1)));
        await f.TickAsync();
        Assert.Equal(["writer|writer@example.test|docs: update history/runbook", "Atlas|atlas@atlas.invalid|chore: snapshot installation"], (await f.AllCommitsAsync())[^2..]);
        Assert.All(await ChangedAsync(f), x => Assert.True(x == BackupManifest.Path || x.StartsWith(".atlas/modules/pages/", StringComparison.Ordinal)
            || x.StartsWith(".atlas/modules/platform/ContentResource/", StringComparison.Ordinal), x));

        // A change outside the pages waits for the interval, then comes as one snapshot of its rows.
        var commits = (await f.AllCommitsAsync()).Count;
        // The check reads PostgreSQL's write counts first (GitBackupService.SnapshotDueAsync); a backend reports them within seconds of
        // its commit, this one at once.
        await f.Pages.Execute("UPDATE \"UserGroup\" SET \"Name\" = 'Provoz' WHERE \"Id\" = :id; SELECT pg_stat_force_next_flush()", group);
        await f.TickAsync();
        Assert.Equal(commits, (await f.AllCommitsAsync()).Count);
        f.Clock.Advance(GitBackupService.SnapshotInterval + TimeSpan.FromSeconds(1));
        await f.TickAsync();
        Assert.Equal(commits + 1, (await f.AllCommitsAsync()).Count);
        var changed = await ChangedAsync(f);
        Assert.Contains($".atlas/secure/identity/UserGroup/{group:D}.json.enc", changed);
        Assert.All(changed, x => Assert.True(x == BackupManifest.Path || x.StartsWith(".atlas/secure/identity/", StringComparison.Ordinal), x));
        // Nothing changed since: the next interval reads the stamps and commits nothing.
        f.Clock.Advance(GitBackupService.SnapshotInterval + TimeSpan.FromSeconds(1));
        await f.TickAsync();
        Assert.Equal(commits + 1, (await f.AllCommitsAsync()).Count);
    }

    [PostgreSqlFact]
    public async Task A_2x_repository_continues_and_the_first_sync_adds_the_installation_without_rewriting_history()
    {
        await using var f = await GitBackupTests.GitFixture.CreateAsync();
        await f.CreateAsync("Runbook", null);
        await EnableAsync(f);
        await f.SyncAsync();
        // The remote as 2.x left it: the same history and marker, no installation data.
        var work = Path.Combine(f.Root, "work");
        await f.GitAsync(f.Root, "clone", "--quiet", f.Url, work);
        await f.GitAsync(work, "rm", "-r", "--quiet", "--ignore-unmatch", ".atlas/modules", ".atlas/secure", ".atlas/blobs", BackupManifest.Path, GitBackupLayout.AttributesPath);
        await f.GitAsync(work, "commit", "--quiet", "-m", "2.x state");
        await f.GitAsync(work, "push", "--quiet", "origin", "HEAD:main");
        var old = await f.RemoteAsync("rev-parse", "main");
        Assert.DoesNotContain(BackupManifest.Path, await f.AllFilesAsync());

        var status = await f.Restarted().SyncAsync(true, default);

        Assert.Null(status!.LastError);
        Assert.Equal("", await f.RemoteAsync("merge-base", "--is-ancestor", old, "main"));
        Assert.Contains(BackupManifest.Path, await f.AllFilesAsync());
        Assert.Equal(old, await f.RemoteAsync("rev-parse", "main~1"));
    }

    private static async Task<string> EnableAsync(GitBackupTests.GitFixture f)
    {
        await f.Service.GenerateKeyAsync(default);
        var saved = await f.Service.SaveSettingsAsync(new GitBackupSettingsRequest(true, f.Url, "main"), default);
        return saved.NewBackupKey!.Key;
    }

    private static async Task<List<string>> ChangedAsync(GitBackupTests.GitFixture f)
        => (await f.RemoteAsync("show", "--name-only", "--format=", "main")).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
}
