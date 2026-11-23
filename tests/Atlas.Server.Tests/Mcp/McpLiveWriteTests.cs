using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Platform.Validation;
using ModelContextProtocol.Client;
using Atlas.Server.Tests.Collaboration;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Mcp;

/// <summary>
/// 3.6-H: an agent's write into a page open in live editing is a collaborator's update. The person's editor gets it on the same generation
/// (no remount), the agent shows among the collaborators while it writes (and its caret where it wrote), the update names its channel and token, and the history splits:
/// what people typed is checkpointed as their revision before the agent's own revision.
/// </summary>
public sealed class McpLiveWriteTests
{
    // "## A / Text A. / ## B / Text B." (heading ids a and b, the editor's Yjs of the page below); Human types " Píše člověk." after "Text A.".
    internal static readonly byte[] Baseline = Convert.FromBase64String(
        "ARCF3JWmCAAHAQdkZWZhdWx0AwdoZWFkaW5nBwCF3JWmCAAGBACF3JWmCAEBQSgAhdyVpggACWhlYWRpbmdJZAF3AWEoAIXclaYIAAVsZXZlbAF9AoeF3JWmCAADCXBhcmFncmFwaAcAhdyVpggFBgQAhdyVpggGB1RleHQgQS6HhdyVpggFAwdoZWFkaW5nBwCF3JWmCA4GBACF3JWmCA8BQigAhdyVpggOCWhlYWRpbmdJZAF3AWIoAIXclaYIDgVsZXZlbAF9AoeF3JWmCA4DCXBhcmFncmFwaAcAhdyVpggTBgQAhdyVpggUB1RleHQgQi4A");
    internal static readonly byte[] Human = Convert.FromBase64String("AQGF3JWmCByEhdyVpggNESBQw63FoWUgxI1sb3bEm2suAA==");
    // What the Node worker of 3.6-H made of Baseline towards this document (section B rewritten); the managed authority writes the same
    // bytes with its client id (ManagedLiveWriteAuthorityTests):
    internal static readonly byte[] Agent = Convert.FromBase64String("AQGi6K7HDADEhdyVpggahdyVpggbDyBwxZllcHNhbCBhZ2VudAA=");
    // {"type":"doc","content":[## A, "Text A.", ## B, "Text B přepsal agent."]}, heading ids a and b.

    [PostgreSqlFact]
    public async Task An_agent_rewrites_a_section_of_an_open_page_as_a_collaborator()
    {
        var authority = new ReplayAuthority();
        await using var f = await McpSite.CreateAsync(services: services => services.AddSingleton<ILiveWriteAuthority>(authority));
        var (live, page, generation) = await OpenLiveAsync(f);
        await using var hub = live.Hub;

        var (tokenId, client) = await McpAttachmentToolTests.DraftClientAsync(f);
        var written = await CallAsync(client, "atlas_update_section", new
        {
            pageId = page.Id, expectedVersion = page.Version, headingId = "b", markdown = "## B\n\nText B přepsal agent.\n"
        });

        Assert.True(written.IsError != true, Text(written));
        // Same generation, the person's editor got the agent's update as a peer's and saw the agent come and go among the collaborators.
        Assert.Equal(generation, await GenerationAsync(f, page.Id));
        // Then its caret right after the text it wrote (" přepsal agent", the update's 14 units), which stays a moment before it goes.
        await CollaborationContentGuardHttpTests.WaitUntilAsync(() => live.Updates.Count == 1 && live.Presence.Count == 3);
        Assert.Equal(Agent, Assert.Single(live.Updates));
        Assert.Contains("· AI (MCP: Claude Code)", live.Presence[0]);
        Assert.DoesNotContain("cursor", live.Presence[0]);
        Assert.Contains("\"cursor\":{\"anchor\":{\"item\":{\"client\":3370890274,\"clock\":13},\"assoc\":-1}", live.Presence[1]);
        Assert.EndsWith("null", live.Presence[2]);
        // The agent's revision (its channel and token) is the live document's checkpoint at the update's sequence.
        Assert.Equal($"2:{f.AdminId}:mcp:{tokenId}", await f.Site.ScalarAsync("""
            SELECT r."Version" || ':' || r."AuthorId" || ':' || r."Channel" || ':' || r."ApiTokenId" FROM "PageRevision" r
            WHERE r."PageId"=@page AND r."ActiveTo" IS NULL
            """, ("page", Guid.Parse(page.Id))));
        Assert.Equal($"1:{f.AdminId}:mcp:{tokenId}|1:1", await f.Site.ScalarAsync("""
            SELECT (SELECT string_agg(u."Sequence" || ':' || u."UserId" || ':' || u."Channel" || ':' || u."ApiTokenId", '|') FROM "CollaborationUpdate" u WHERE u."PageId"=@page)
                || '|' || c."Sequence" || ':' || c."LastSavedSequence" FROM "PageCollaboration" c WHERE c."PageId"=@page
            """, ("page", Guid.Parse(page.Id))));
    }

    [PostgreSqlFact]
    public async Task A_live_write_that_cannot_be_appended_lands_as_before()
    {
        var authority = new ReplayAuthority { Fail = true };
        await using var f = await McpSite.CreateAsync(services: services => services.AddSingleton<ILiveWriteAuthority>(authority));
        var (live, page, generation) = await OpenLiveAsync(f);
        await using var hub = live.Hub;
        var (_, client) = await McpAttachmentToolTests.DraftClientAsync(f);

        var written = await CallAsync(client, "atlas_update_section", new
        {
            pageId = page.Id, expectedVersion = page.Version, headingId = "b", markdown = "## B\n\nText B přepsal agent.\n"
        });

        // The revision stands and the generation moves (editors reload it), as every write did before 3.6; nobody had anything unsaved.
        Assert.True(written.IsError != true, Text(written));
        Assert.NotEqual(generation, await GenerationAsync(f, page.Id));
        Assert.Equal(2L, await f.Site.ScalarAsync("SELECT count(*) FROM \"PageRevision\" WHERE \"PageId\"=@page", ("page", Guid.Parse(page.Id))));
        Assert.Empty(live.Updates);
    }

    /// <summary>
    /// The server merges without garbage collection, so what an agent rewrites stays in the live state as deleted content until a
    /// browser pushes a snapshot. A peer that never does (a reader, a tab that does not snapshot) and an agent rewriting a section
    /// over and over: the state grows only up to the document limit, where the live write cannot be appended and the generation is
    /// reset to the agent's revision (DocumentCompactor's comment). Every write lands; the state stays bounded.
    /// </summary>
    [PostgreSqlFact]
    public async Task Agent_rewrites_without_a_browser_snapshot_keep_the_live_state_within_the_document_limit()
    {
        const long Limit = 8 * 1024;
        await using var f = await McpSite.CreateAsync(services: services =>
        {
            CollaborationContentGuardHttpTests.BrowserSeeds(services);
            ((MarkdownSyncOptions)services.Single(service => service.ServiceType == typeof(MarkdownSyncOptions)).ImplementationInstance!).MaxDocumentBytes = Limit;
        });
        var (live, page, generation) = await OpenLiveAsync(f);
        await using var hub = live.Hub;
        var (_, client) = await McpAttachmentToolTests.DraftClientAsync(f);
        var id = ("page", (object)Guid.Parse(page.Id));

        var text = "";
        for (var round = 0; round < 20; round++)
        {
            text = $"Přepis {round}: " + new string((char)('a' + round), 600);
            var version = Convert.ToInt32(await f.Site.ScalarAsync("SELECT \"Version\" FROM \"Page\" WHERE \"Id\"=@page", id));
            var written = await CallAsync(client, "atlas_update_section", new { pageId = page.Id, expectedVersion = version, headingId = "b", markdown = $"## B\n\n{text}\n" });
            Assert.True(written.IsError != true, $"round {round}: {Text(written)}");
            // The live state of the current generation: its snapshot and the update log above it.
            Assert.True(Convert.ToInt64(await f.Site.ScalarAsync("""
                SELECT coalesce(length(c."State"), 0) + coalesce((SELECT sum(length(u."Payload")) FROM "CollaborationUpdate" u
                    WHERE u."PageId" = c."PageId" AND u."Generation" = c."Generation" AND u."Sequence" > c."SnapshotSequence"), 0)
                FROM "PageCollaboration" c WHERE c."PageId"=@page
                """, id)) <= Limit, $"round {round}");
        }

        // The rewrites first reached the browser as updates, then (at the limit) moved the page to a fresh generation.
        Assert.InRange(live.Updates.Count, 5, 19);
        Assert.NotEqual(generation, await GenerationAsync(f, page.Id));
        Assert.Contains(text, (string)(await f.Site.ScalarAsync("SELECT r.\"ContentJson\" FROM \"PageRevision\" r WHERE r.\"PageId\"=@page AND r.\"ActiveTo\" IS NULL", id))!);
    }

    [PostgreSqlFact]
    public async Task People_keep_typing_while_an_agent_writes_and_history_splits_at_the_agent()
    {
        await using var f = await McpSite.CreateAsync(services: CollaborationContentGuardHttpTests.BrowserSeeds);
        var (live, page, generation) = await OpenLiveAsync(f);
        await using var hub = live.Hub;
        // The person types in section A and nothing is saved yet.
        Assert.Equal(1, await live.Hub.InvokeAsync<long>("SendUpdate", live.DocumentId, Guid.NewGuid().ToString("D"), Human));

        // The agent read version 1 and rewrites section B.
        var (_, client) = await McpAttachmentToolTests.DraftClientAsync(f);
        var written = await CallAsync(client, "atlas_update_section", new
        {
            pageId = page.Id, expectedVersion = page.Version, headingId = "b", markdown = "## B\n\nText B přepsal agent.\n"
        });

        Assert.True(written.IsError != true, Text(written));
        Assert.Equal(generation, await GenerationAsync(f, page.Id));
        await CollaborationContentGuardHttpTests.WaitUntilAsync(() => live.Updates.Count == 1);
        var shown = YjsDocumentMaterializer.Builtin.Materialize(Baseline, [Human, live.Updates[0]]);
        Assert.Contains("Text A. Píše člověk.", shown);
        Assert.Contains("Text B přepsal agent.", shown);
        // History: the page, what the person typed (their checkpoint, web), the agent's change alone (mcp).
        Assert.Equal($"1:|2:web:{live.UserId}:Text A. Píše člověk.:Text B.|3:mcp:{f.AdminId}:Text A. Píše člověk.:Text B přepsal agent.", await f.Site.ScalarAsync("""
            SELECT string_agg(r."Version" || ':' || CASE WHEN r."Version" = 1 THEN '' ELSE r."Channel" || ':' || r."AuthorId" || ':'
                || CASE WHEN r."Markdown" LIKE '%Píše člověk%' THEN 'Text A. Píše člověk.' ELSE 'Text A.' END || ':'
                || CASE WHEN r."Markdown" LIKE '%přepsal agent%' THEN 'Text B přepsal agent.' ELSE 'Text B.' END END, '|' ORDER BY r."Version")
            FROM "PageRevision" r WHERE r."PageId"=@page
            """, ("page", Guid.Parse(page.Id))));
        Assert.Equal("2:2", await f.Site.ScalarAsync("""SELECT "Sequence" || ':' || "LastSavedSequence" FROM "PageCollaboration" WHERE "PageId"=@page""",
            ("page", Guid.Parse(page.Id))));

        // An agent still holding version 1: a new section passes (nothing it touches changed), section A was changed by the person.
        Assert.True((await CallAsync(client, "atlas_insert_section", new { pageId = page.Id, expectedVersion = page.Version, heading = "C", level = 2, markdown = "Text C." }))
            .IsError != true);
        Assert.Equal("target_changed", ErrorCode(await CallAsync(client, "atlas_update_section", new
        {
            pageId = page.Id, expectedVersion = page.Version, headingId = "a", markdown = "## A\n\nPřepsáno.\n"
        })));
        // A placeholder ⟦c:N⟧ kept from that read no longer matches the page people changed since: a changed target as well, read again.
        Assert.Equal("target_changed", ErrorCode(await CallAsync(client, "atlas_update_section", new
        {
            pageId = page.Id, expectedVersion = page.Version, headingId = "b", markdown = "## B\n\n⟦c:7⟧\n\nPřepsáno.\n"
        })));
        // The same race over REST (PUT /api/v1/pages/{id}): 409 target_changed so the client retries, not 400 components_changed.
        var rest = f.Site.Client();
        rest.DefaultRequestHeaders.Authorization = new("Bearer", (await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["rest"], "rest")).Secret);
        var put = await AtlasHttpSite.SendAsync(rest, HttpMethod.Put, $"/api/v1/pages/{page.Id}",
            new { expectedVersion = page.Version, markdown = "## A\n\nText A. Píše člověk.\n\n⟦c:7⟧\n\n## B\n\nText B.\n" });
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Contains("target_changed", await put.Content.ReadAsStringAsync());

        // The agent's rewrite of B undone as a whole: B is back, the person's text and the later section C stay, live again.
        var agentRevision = (Guid)(await f.Site.ScalarAsync("SELECT \"Id\" FROM \"PageRevision\" WHERE \"PageId\"=@page AND \"Version\"=3", ("page", Guid.Parse(page.Id))))!;
        var undone = await CallAsync(client, "atlas_undo_change", new { pageId = page.Id, revisionId = agentRevision.ToString(), expectedVersion = 4 });
        Assert.True(undone.IsError != true, Text(undone));
        var markdown = (string)(await f.Site.ScalarAsync("SELECT \"Markdown\" FROM \"PageRevision\" WHERE \"PageId\"=@page AND \"ActiveTo\" IS NULL", ("page", Guid.Parse(page.Id))))!;
        Assert.Contains("Text A. Píše člověk.", markdown);
        Assert.Contains("Text B.", markdown);
        Assert.DoesNotContain("přepsal agent", markdown);
        Assert.Contains("Text C.", markdown);
        Assert.Equal(generation, await GenerationAsync(f, page.Id));
    }

    /// <summary>
    /// A live write the actor may not make touches nothing live before its refusal: no agent among the collaborators, no checkpoint of
    /// the people's unsaved edits (ReplayAuthority.CheckpointAsync throws), no new revision. Every live-write route and MCP tool as a reader,
    /// without access, and with a token whose space is out of AI.
    /// </summary>
    [PostgreSqlFact]
    public async Task A_live_write_without_the_right_to_write_touches_nothing_live()
    {
        await using var f = await McpSite.CreateAsync(services: services => services.AddSingleton<ILiveWriteAuthority>(new ReplayAuthority()));
        var (live, page, _) = await OpenLiveAsync(f);
        await using var hub = live.Hub;
        Assert.Equal(1, await live.Hub.InvokeAsync<long>("SendUpdate", live.DocumentId, Guid.NewGuid().ToString("D"), Human));
        var space = (string)(await f.Site.ScalarAsync("SELECT \"SpaceId\"::text FROM \"Page\" WHERE \"Id\"=@page", ("page", Guid.Parse(page.Id))))!;
        var revision = await f.Site.ScalarAsync("SELECT \"Id\" FROM \"PageRevision\" WHERE \"PageId\"=@page AND \"ActiveTo\" IS NULL", ("page", Guid.Parse(page.Id)));
        var (reader, readerId) = await f.UserAsync("reader", "write");
        await f.GrantAsync(space, readerId, "Reader");
        var (stranger, _) = await f.UserAsync("stranger", "write");
        var undo = $"/api/pages/{page.Id}/history/{revision}/undo";
        var arguments = new { pageId = page.Id, expectedVersion = page.Version, revisionId = revision!.ToString(), headingId = "b", markdown = "## B\n\nX.\n" };

        foreach (var (client, name) in new[] { (reader, "reader"), (stranger, "stranger") })
        {
            Assert.Contains((await AtlasHttpSite.SendAsync(client, HttpMethod.Post, undo, new { expectedVersion = page.Version })).StatusCode,
                new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
            var api = f.Site.Client();
            api.DefaultRequestHeaders.Authorization = new("Bearer", (await IssueAsync(client, f.Site.Password, ["read", "write"], ["rest"], name)).Secret);
            Assert.Contains((await AtlasHttpSite.SendAsync(api, HttpMethod.Post, $"/api/v1/pages/{page.Id}/history/{revision}/undo",
                new { expectedVersion = page.Version })).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
            var mcp = await f.ClientAsync((await IssueAsync(client, f.Site.Password, ["read", "write"], ["mcp"], name)).Secret);
            foreach (var tool in McpLiveWrites.Tools)
            {
                Assert.True((await CallAsync(mcp, tool, arguments)).IsError, $"{name}: {tool}");
            }
        }
        await f.ExcludeFromAiAsync(space);
        var (_, admin) = await McpAttachmentToolTests.DraftClientAsync(f);
        foreach (var tool in McpLiveWrites.Tools)
        {
            Assert.True((await CallAsync(admin, tool, arguments)).IsError, $"excluded: {tool}");
        }

        await Task.Delay(300);
        Assert.Empty(live.Presence);
        Assert.Equal("1:1:0", await f.Site.ScalarAsync("""
            SELECT (SELECT count(*) FROM "PageRevision" r WHERE r."PageId"=@page) || ':' || c."Sequence" || ':' || coalesce(c."LastSavedSequence", 0)
            FROM "PageCollaboration" c WHERE c."PageId"=@page
            """, ("page", Guid.Parse(page.Id))));
    }

    /// <summary>
    /// A person types word by word into section A while three agents insert three sections each, all at once: every word and every
    /// section lands (nothing an agent writes takes back what the person typed meanwhile, nor the other way), the person's words in
    /// their order, the page stays on its generation, and the whole run is bounded. The person's updates are made as a browser makes
    /// them, against what it has seen so far (its own words and the agents' updates that reached it), so they are concurrent with the agents'.
    /// </summary>
    [PostgreSqlFact]
    public async Task People_and_agents_writing_one_page_at_once_lose_nothing()
    {
        await using var f = await McpSite.CreateAsync(services: CollaborationContentGuardHttpTests.BrowserSeeds);
        var (live, page, generation) = await OpenLiveAsync(f);
        await using var hub = live.Hub;
        var agents = new List<McpClient>();
        for (var agent = 0; agent < 3; agent++) agents.Add((await McpAttachmentToolTests.DraftClientAsync(f)).Client);
        var schema = new MarkdownDocumentSchema(HostDocumentValidator.Instance.Schema.SchemaRevision, HostDocumentValidator.Instance.Schema.Profile,
            HostDocumentValidator.Instance.Schema.Plugins.Values.Select(plugin => new MarkdownSchemaPlugin(plugin.Id, plugin.Version)).ToArray());
        var authority = new ManagedLiveWriteAuthority();
        var mine = new List<byte[]>();
        const int Words = 20;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        async Task TypeAsync()
        {
            for (var word = 0; word < Words; word++)
            {
                byte[][] seen;
                lock (live.Updates) seen = [.. mine, .. live.Updates];
                var shown = System.Text.Json.Nodes.JsonNode.Parse(YjsDocumentMaterializer.Builtin.Materialize(Baseline, seen.Select(update => (ReadOnlyMemory<byte>)update)))!;
                var text = shown["content"]!.AsArray().First(block => block!["content"]?[0]?["text"]?.GetValue<string>().StartsWith("Text A.") == true)!["content"]![0]!;
                text["text"] = text["text"]!.GetValue<string>() + $" w{word}";
                using var target = JsonDocument.Parse(shown.ToJsonString());
                var update = await authority.WriteAsync(schema, Baseline, seen, target.RootElement, 0, null, CancellationToken.None);
                await live.Hub.InvokeAsync<long>("SendUpdate", live.DocumentId, Guid.NewGuid().ToString("D"), update);
                lock (live.Updates) mine.Add(update);
            }
        }
        var latencies = new System.Collections.Concurrent.ConcurrentBag<long>();
        async Task WriteAsync(McpClient client, int agent)
        {
            for (var section = 0; section < 3; section++)
            {
                var started = clock.ElapsedMilliseconds;
                var written = await CallAsync(client, "atlas_insert_section", new
                {
                    pageId = page.Id, expectedVersion = page.Version, heading = $"Agent {agent}.{section}", level = 2, markdown = $"Píše agent {agent}, oddíl {section}."
                });
                latencies.Add(clock.ElapsedMilliseconds - started);
                Assert.True(written.IsError != true, $"agent {agent}.{section}: {Text(written)}");
            }
        }

        await Task.WhenAll([TypeAsync(), .. agents.Select((client, agent) => WriteAsync(client, agent))]);
        await CollaborationContentGuardHttpTests.WaitUntilAsync(() => { lock (live.Updates) return live.Updates.Count == 9; });
        var elapsed = clock.ElapsedMilliseconds;

        Assert.Equal(generation, await GenerationAsync(f, page.Id));
        byte[][] all;
        lock (live.Updates) all = [.. mine, .. live.Updates];
        var final = YjsDocumentMaterializer.Builtin.Materialize(Baseline, all.Select(update => (ReadOnlyMemory<byte>)update));
        Assert.Contains("Text A." + string.Concat(Enumerable.Range(0, Words).Select(word => $" w{word}")), final);
        Assert.Contains("Text B.", final);
        for (var agent = 0; agent < 3; agent++)
            for (var section = 0; section < 3; section++)
                Assert.Contains($"Píše agent {agent}, oddíl {section}.", final);
        // Bounded: the agents' writes queue at the page's gate one after another, the person's words in between.
        Assert.True(elapsed < 60_000, $"{elapsed} ms");
        Console.WriteLine($"People_and_agents_writing_one_page_at_once: {Words} words + 9 agent writes in {elapsed} ms; agent write max {latencies.Max()} ms, median {latencies.Order().ElementAt(latencies.Count / 2)} ms");
    }

    /// <summary>The page "## A / Text A. / ## B / Text B.", open in a person's browser with its live document seeded (Baseline).</summary>
    internal static async Task<(LiveEditor Live, (string Id, int Version) Page, object? Generation)> OpenLiveAsync(McpSite f)
    {
        var space = await f.FirstSpaceAsync();
        var (_, editorId) = await f.UserAsync("editor", level: null);
        await f.GrantAsync(space, editorId, "Writer");
        var page = await PageAsync(f.Admin, space, "Živá", Rich(Heading(2, "a", "A"), Paragraph("Text A."), Heading(2, "b", "B"), Paragraph("Text B.")),
            "## A\n\nText A.\n\n## B\n\nText B.");
        var cookies = new CookieContainer();
        var browser = f.Site.CreateDefaultClient(CollaborationContentGuardHttpTests.BaseAddress, new CookieContainerHandler(cookies));
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(browser, HttpMethod.Post, "/api/auth/login", new { username = f.Login(editorId), password = f.Site.Password }), HttpStatusCode.OK);
        var documentId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(browser, HttpMethod.Post, $"/api/pages/{page.Id}/collaboration"), HttpStatusCode.OK))["documentId"]!.GetValue<string>();
        var hub = CollaborationContentGuardHttpTests.Connect(f.Site, cookies);
        var live = new LiveEditor(hub, documentId, editorId);
        hub.On<string, byte[], long>("ReceiveUpdate", (_, update, _) => { lock (live.Updates) live.Updates.Add(update); });
        hub.On<string, byte[]>("ReceiveAwareness", (_, payload) => { lock (live.Presence) live.Presence.Add(Encoding.UTF8.GetString(payload)); });
        await hub.StartAsync();
        Assert.True((await CollaborationContentGuardHttpTests.JoinAsync(f.Site, hub, documentId)).IsSnapshotter);
        await hub.InvokeAsync("PushSnapshot", documentId, 0L, Baseline, CollaborationContentGuardHttpTests.Projections(0, Baseline));
        return (live, page, await GenerationAsync(f, page.Id));
    }

    private static Task<object?> GenerationAsync(McpSite f, string pageId)
        => f.Site.ScalarAsync("SELECT \"Generation\" FROM \"PageCollaboration\" WHERE \"PageId\"=@page", ("page", Guid.Parse(pageId)));

    internal sealed record LiveEditor(HubConnection Hub, string DocumentId, string UserId)
    {
        public List<byte[]> Updates { get; } = [];
        public List<string> Presence { get; } = [];
    }

    /// <summary>Replays the authority's update for the one target these tests write (the managed authority itself runs in the test above it).</summary>
    private sealed class ReplayAuthority : ILiveWriteAuthority
    {
        public bool Fail { get; init; }

        public Task<DocSnapshot> CheckpointAsync(MarkdownDocumentSchema schema, byte[] snapshot, IReadOnlyList<byte[]> updates, long sequence, CancellationToken ct)
            => throw new InvalidOperationException("Nothing is unsaved in these tests.");

        public Task<DocSnapshot> SeedAsync(MarkdownDocumentSchema schema, JsonElement document, CancellationToken ct)
            => throw new InvalidOperationException("These tests seed through the browser (OpenLiveAsync).");

        public Task<byte[]> WriteAsync(MarkdownDocumentSchema schema, byte[] snapshot, IReadOnlyList<byte[]> updates, JsonElement target, long sequence,
            CancellationToken ct)
        {
            Assert.Equal(Baseline, snapshot);
            // The page's revision with section B rewritten.
            Assert.Contains("\"Text A.\"", target.GetRawText());
            Assert.Contains("Text B přepsal agent.", target.GetRawText());
            return Fail ? throw new InvalidDataException("[AUTHORITY_UNAVAILABLE] test") : Task.FromResult(Agent);
        }
    }
}
