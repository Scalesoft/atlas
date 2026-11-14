using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Wolverine;
using Xunit;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>
/// 3.6, owner 2026-11-05 („server ukládá sám“): the server stores a live page from its log when the last editor leaves (campaign F3: the
/// tail stayed in CollaborationUpdate until someone reopened the page) and, while people keep typing, at the latest
/// MarkdownSyncOptions.ServerCheckpointInterval after the first unsaved update (F4: no browser autosave got through), as the person who
/// typed, without duplicate revisions next to a browser's own checkpoint.
/// </summary>
public sealed class ServerCheckpointTests
{
    private static readonly YjsDocumentWriter Writer = new(HostDocumentValidator.Instance.Schema);
    private static readonly YjsDocumentMaterializer Reader = new(HostDocumentValidator.Instance.Schema);
    private static readonly string Plugins = JsonSerializer.Serialize(HostDocumentValidator.Instance.Schema.Plugins.Values.Select(x => new { id = x.Id, version = x.Version }));

    [PostgreSqlFact]
    public async Task The_tail_typed_before_the_last_editor_leaves_becomes_their_revision_and_frees_non_live_writes()
    {
        await using var f = await McpSite.CreateAsync(services: CollaborationContentGuardHttpTests.BrowserSeeds);
        var (live, page, generation) = await McpLiveWriteTests.OpenLiveAsync(f);
        Assert.Equal(1L, await live.Hub.InvokeAsync<long>("SendUpdate", live.DocumentId, Guid.NewGuid().ToString("D"), McpLiveWriteTests.Human));

        await live.Hub.DisposeAsync(); // the tab closes; the interval (30 s) is far away

        await WaitForAsync(async () => await SavedAsync(f, page.Id) == "1:1");
        Assert.Equal($"2:web:{live.UserId}:yes", await f.Site.ScalarAsync("""
            SELECT r."Version" || ':' || r."Channel" || ':' || r."AuthorId" || ':' || CASE WHEN r."Markdown" LIKE '%Text A. Píše člověk.%' THEN 'yes' ELSE 'no' END
            FROM "PageRevision" r WHERE r."PageId"=@page AND r."ActiveTo" IS NULL
            """, ("page", Guid.Parse(page.Id))));
        Assert.Equal(generation, await f.Site.ScalarAsync("SELECT \"Generation\" FROM \"PageCollaboration\" WHERE \"PageId\"=@page", ("page", Guid.Parse(page.Id))));
        // A save without the live session (another person, so RequireNoForeignUnsaved decides) is no longer refused collaboration_unsaved.
        var saved = await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/pages/{page.Id}", new
        {
            title = "Živá", contentJson = Rich("Po odchodu."), markdown = "Po odchodu.", expectedVersion = 2
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    [PostgreSqlFact]
    public async Task People_typing_on_and_on_get_a_revision_within_the_interval_without_any_browser_save()
    {
        await using var f = await McpSite.CreateAsync(services: services =>
        {
            CollaborationContentGuardHttpTests.BrowserSeeds(services);
            services.Replace(ServiceDescriptor.Singleton(new MarkdownSyncOptions { ServerCheckpointInterval = TimeSpan.FromSeconds(1) }));
        });
        var (live, page, _) = await McpLiveWriteTests.OpenLiveAsync(f);
        await using var hub = live.Hub;
        var stored = new List<long>();
        live.Hub.On<string, long>("SnapshotStored", (_, sequence) => { lock (stored) stored.Add(sequence); });
        var updates = new List<byte[]>();
        var started = DateTime.UtcNow;
        // Typing for three intervals, an update every 100 ms: the snapshot never catches up, and no browser saves. A loaded
        // CI worker delivers a checkpoint later than its interval (run 151: one in 3 s), so the typing goes on until two landed.
        bool Typing()
        {
            var elapsed = DateTime.UtcNow - started;
            lock (stored) return elapsed < TimeSpan.FromSeconds(3) || (stored.Count < 2 && elapsed < TimeSpan.FromSeconds(30));
        }
        for (var n = 1; Typing(); n++)
        {
            var update = Typed(updates, $"Text A. slovo{n}");
            Assert.Equal(n, await live.Hub.InvokeAsync<long>("SendUpdate", live.DocumentId, Guid.NewGuid().ToString("D"), update));
            updates.Add(update);
            await Task.Delay(100);
        }

        // At least two checkpoints landed while typing went on (one per interval), each the person's, in one version (3.6-C rule 7).
        var revisions = await f.Site.ScalarAsync("""
            SELECT count(*) || ':' || count(DISTINCT "GroupId") || ':' || bool_and("Channel" = 'web' AND "AuthorId" = @author)
            FROM "PageRevision" WHERE "PageId"=@page AND "Version" > 1
            """, ("page", Guid.Parse(page.Id)), ("author", Guid.Parse(live.UserId)));
        var parts = ((string)revisions!).Split(':');
        Assert.True(int.Parse(parts[0]) >= 2, (string)revisions);
        Assert.Equal(("1", "true"), (parts[1], parts[2]));
        // The newest has a word typed in the last interval.
        var markdown = (string)(await f.Site.ScalarAsync("SELECT \"Markdown\" FROM \"PageRevision\" WHERE \"PageId\"=@page AND \"ActiveTo\" IS NULL",
            ("page", Guid.Parse(page.Id))))!;
        Assert.Contains("Text A. slovo", markdown);
        // The editors were told each checkpoint's head is stored (no browser pushed a snapshot here), so a waiting save goes at once.
        lock (stored) Assert.True(stored.Count >= 2 && stored.All(sequence => sequence >= 1), string.Join(",", stored));
    }

    [PostgreSqlFact]
    public async Task A_browser_checkpoint_racing_the_server_checkpoint_stores_one_revision_and_loses_nothing()
    {
        await using var f = await McpSite.CreateAsync(services: CollaborationContentGuardHttpTests.BrowserSeeds);
        var (live, page, generation) = await McpLiveWriteTests.OpenLiveAsync(f);
        await using var hub = live.Hub;
        var update = Typed([], "Text A. Souběh.");
        Assert.Equal(1L, await live.Hub.InvokeAsync<long>("SendUpdate", live.DocumentId, Guid.NewGuid().ToString("D"), update));
        // The browser's snapshot of the head, then its checkpoint with exactly that document, while the server checkpoints twice.
        var state = YjsUpdateMerger.Merge([McpLiveWriteTests.Baseline, update], 16 * 1024 * 1024);
        await live.Hub.InvokeAsync("PushSnapshot", live.DocumentId, 1L, state, CollaborationContentGuardHttpTests.Projections(1, state));
        var document = Reader.Materialize(state);
        var browser = f.Site.Client();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(browser, HttpMethod.Post, "/api/auth/login", new { username = f.Login(live.UserId), password = f.Site.Password }),
            HttpStatusCode.OK);
        var pageId = Guid.Parse(page.Id);
        var server = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await using var scope = f.Site.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(new CheckpointLivePage(pageId, (Guid)generation!));
        }));
        var client = AtlasHttpSite.SendAsync(browser, HttpMethod.Put, $"/api/pages/{page.Id}", new
        {
            title = "Živá", contentJson = $"{{\"format\":\"atlas-rich\",\"version\":1,\"plugins\":{Plugins},\"document\":{document}}}", markdown = "",
            expectedVersion = page.Version, collaborationGeneration = generation
        });
        await Task.WhenAll(server);
        // The browser's save is the same document: it goes through, or (its version taken meanwhile) changes nothing.
        var answer = await client;
        Assert.True(answer.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, await answer.Content.ReadAsStringAsync());

        Assert.Equal("1:1", await SavedAsync(f, page.Id));
        Assert.Equal("2:1", await f.Site.ScalarAsync("""
            SELECT count(*) || ':' || count(*) FILTER (WHERE "ActiveTo" IS NULL AND "Markdown" LIKE '%Text A. Souběh.%') FROM "PageRevision" WHERE "PageId"=@page
            """, ("page", pageId)));
    }

    /// <summary>The one who typed last lost the right to edit: the checkpoint goes as the newest author of the tail who still may, at once.</summary>
    [PostgreSqlFact]
    public async Task A_last_typist_without_edit_rights_hands_the_checkpoint_to_the_previous_author()
    {
        await using var f = await McpSite.CreateAsync(services: CollaborationContentGuardHttpTests.BrowserSeeds);
        var (live, page, generation) = await McpLiveWriteTests.OpenLiveAsync(f);
        await using var hub = live.Hub;
        var first = Typed([], "Text A. Prvni.");
        Assert.Equal(1L, await live.Hub.InvokeAsync<long>("SendUpdate", live.DocumentId, Guid.NewGuid().ToString("D"), first));
        var (second, secondId) = await SecondWriterAsync(f, live.DocumentId);
        await using var _ = second;
        Assert.Equal(2L, await second.InvokeAsync<long>("SendUpdate", live.DocumentId, Guid.NewGuid().ToString("D"), Typed([first], "Text A. Prvni. Druhy.")));
        await RevokeAsync(f, secondId);

        await CheckpointAsync(f, page.Id, generation);

        Assert.Equal("2:2", await SavedAsync(f, page.Id));
        Assert.Equal($"2:web:{live.UserId}:yes", await f.Site.ScalarAsync("""
            SELECT r."Version" || ':' || r."Channel" || ':' || r."AuthorId" || ':' || CASE WHEN r."Markdown" LIKE '%Prvni. Druhy.%' THEN 'yes' ELSE 'no' END
            FROM "PageRevision" r WHERE r."PageId"=@page AND r."ActiveTo" IS NULL
            """, ("page", Guid.Parse(page.Id))));
    }

    /// <summary>„Jedno sezení je jedna verze se všemi autory“: two people typing in different paragraphs at once are both authors of the
    /// checkpoint, saved as the one who typed last, in the history API the panel reads.</summary>
    [PostgreSqlFact]
    public async Task Everyone_whose_live_updates_a_checkpoint_holds_is_its_contributor()
    {
        await using var f = await McpSite.CreateAsync(services: CollaborationContentGuardHttpTests.BrowserSeeds);
        var (live, page, generation) = await McpLiveWriteTests.OpenLiveAsync(f);
        await using var hub = live.Hub;
        var first = Typed([], "Text A. Píše první.");
        Assert.Equal(1L, await live.Hub.InvokeAsync<long>("SendUpdate", live.DocumentId, Guid.NewGuid().ToString("D"), first));
        var (second, secondId) = await SecondWriterAsync(f, live.DocumentId);
        await using var _ = second;
        var other = Typed([first], "Text B. Píše druhý.", paragraph: 3);
        Assert.Equal(2L, await second.InvokeAsync<long>("SendUpdate", live.DocumentId, Guid.NewGuid().ToString("D"), other));
        // The first types again, last: two updates of one person count once.
        Assert.Equal(3L, await live.Hub.InvokeAsync<long>("SendUpdate", live.DocumentId, Guid.NewGuid().ToString("D"),
            Typed([first, other], "Text A. Píše první znovu.")));

        await CheckpointAsync(f, page.Id, generation);

        Assert.Equal("3:3", await SavedAsync(f, page.Id));
        var version = (await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/pages/{page.Id}/history"), HttpStatusCode.OK))["items"]![0]!;
        Assert.Equal((2, live.UserId), (version["version"]!.GetValue<int>(), version["authorId"]!.GetValue<string>()));
        Assert.Equal(new[] { (live.UserId, 1), (secondId, 1) }.OrderBy(x => x.Item1),
            version["changes"]!["contributors"]!.AsArray().Select(x => (x!["id"]!.GetValue<string>(), x["checkpoints"]!.GetValue<int>())).OrderBy(x => x.Item1));
    }

    /// <summary>Nobody of the tail may edit any more: the message ends without a failure (no retries into a dead letter) and the tail
    /// stays durable in the log for the next editor.</summary>
    [PostgreSqlFact]
    public async Task A_tail_nobody_may_save_any_more_waits_in_the_log_without_retries()
    {
        await using var f = await McpSite.CreateAsync(services: CollaborationContentGuardHttpTests.BrowserSeeds);
        var (live, page, generation) = await McpLiveWriteTests.OpenLiveAsync(f);
        await using var hub = live.Hub;
        Assert.Equal(1L, await live.Hub.InvokeAsync<long>("SendUpdate", live.DocumentId, Guid.NewGuid().ToString("D"), McpLiveWriteTests.Human));
        await RevokeAsync(f, live.UserId);

        await CheckpointAsync(f, page.Id, generation); // does not throw

        Assert.Equal("1:0", await SavedAsync(f, page.Id));
        Assert.Equal("1:1", await f.Site.ScalarAsync("""
            SELECT (SELECT count(*) FROM "PageRevision" WHERE "PageId"=@page) || ':' || (SELECT count(*) FROM "CollaborationUpdate" WHERE "PageId"=@page)
            """, ("page", Guid.Parse(page.Id))));
    }

    private static async Task CheckpointAsync(McpSite f, string pageId, object? generation)
    {
        await using var scope = f.Site.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(new CheckpointLivePage(Guid.Parse(pageId), (Guid)generation!));
    }

    /// <summary>Another writer of the page's space, joined to the live document in a browser of its own.</summary>
    private static async Task<(HubConnection Hub, string Id)> SecondWriterAsync(McpSite f, string documentId)
    {
        var space = await f.FirstSpaceAsync();
        var (_, id) = await f.UserAsync("second", level: null);
        await f.GrantAsync(space, id, "Writer");
        var cookies = new CookieContainer();
        var browser = f.Site.CreateDefaultClient(CollaborationContentGuardHttpTests.BaseAddress, new Microsoft.AspNetCore.Mvc.Testing.Handlers.CookieContainerHandler(cookies));
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(browser, HttpMethod.Post, "/api/auth/login", new { username = f.Login(id), password = f.Site.Password }), HttpStatusCode.OK);
        var hub = CollaborationContentGuardHttpTests.Connect(f.Site, cookies);
        await hub.StartAsync();
        await CollaborationContentGuardHttpTests.JoinAsync(f.Site, hub, documentId);
        return (hub, id);
    }

    /// <summary>The account's grants in the space end (a space administrator took them).</summary>
    private static Task RevokeAsync(McpSite f, string userId)
        => f.Site.ScalarAsync("""
            UPDATE "SpaceGrant" SET "ActiveTo" = greatest(clock_timestamp(), "ActiveFrom" + interval '1 microsecond') WHERE "UserId"=@user AND "ActiveTo" IS NULL
            """, ("user", Guid.Parse(userId)));

    /// <summary>The person's next keystrokes: the paragraph "Text A." of McpLiveWriteTests.Baseline (or "Text B.", <paramref name="paragraph"/> 3)
    /// becomes <paramref name="text"/>.</summary>
    private static byte[] Typed(IReadOnlyList<byte[]> before, string text, int paragraph = 1)
    {
        var json = Reader.Materialize(McpLiveWriteTests.Baseline, before.Select(update => (ReadOnlyMemory<byte>)update));
        var current = JsonDocument.Parse(json).RootElement.GetProperty("content")[paragraph].GetProperty("content")[0].GetProperty("text").GetString()!;
        using var target = JsonDocument.Parse(json.Replace(JsonSerializer.Serialize(current), JsonSerializer.Serialize(text), StringComparison.Ordinal));
        return Writer.Write(McpLiveWriteTests.Baseline, before.Select(update => (ReadOnlyMemory<byte>)update), target.RootElement, 4711).Update;
    }

    private static string Rich(string text)
        => $$$"""{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"{{{text}}}"}]}]}}""";

    private static async Task<string?> SavedAsync(McpSite f, string pageId)
        => (string?)await f.Site.ScalarAsync("""SELECT "Sequence" || ':' || coalesce("LastSavedSequence", 0) FROM "PageCollaboration" WHERE "PageId"=@page""",
            ("page", Guid.Parse(pageId)));

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        Assert.True(await condition(), "condition not met within 10 s");
    }
}
