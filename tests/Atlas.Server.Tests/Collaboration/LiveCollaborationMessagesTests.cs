using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.Collaboration.Domain;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>
/// 3.6, the server without Node.js: the collaboration messages end to end over the hub (MarkdownSyncHub) with the managed authority. The
/// server seeds the page, so editors join it and never seed; updates are relayed with their sequence, a repeated update id is the same
/// update, a reconnecting editor catches up; an agent's MCP write lands as a collaborator's update next to people's concurrent typing in
/// the same and in neighbouring blocks, with nothing lost or doubled, shows the agent among the collaborators and in history, and is
/// undone as a whole. Oversized, schema-violating and unknown-plugin input is refused and the stored state stays as it was; an editor
/// holding a generation the server replaced is refused and the new one is the server's seed of the new revision. Editors type through
/// the same Yjs writer the server uses (byte parity with y-prosemirror: tests/Atlas.Yjs.Tests), each with a client id of its own.
/// </summary>
public sealed class LiveCollaborationMessagesTests
{
    private static readonly MarkdownDocumentSchema Schema = CollaborationSchema.KnownHostSchemas[^1];
    private static readonly YjsDocumentMaterializer Host = new(HostDocumentValidator.Instance.Schema);
    private static readonly YjsDocumentWriter Writer = new(HostDocumentValidator.Instance.Schema);

    [PostgreSqlFact]
    public async Task Two_people_and_an_agent_write_one_page_and_every_message_arrives_once()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await SpaceAsync(f);
        var page = await PageAsync(f.Admin, space, "Souběh", Rich(Heading(2, "a", "A"), Paragraph("Text A."), Heading(2, "b", "B"), Paragraph("Text B."),
            Heading(2, "c", "C"), Paragraph("Text C.")), "## A\n\nText A.\n\n## B\n\nText B.\n\n## C\n\nText C.");
        var anna = await OpenAsync(f, space, "anna", page.Id, 101);
        var boris = await OpenAsync(f, space, "boris", page.Id, 202);
        await using var annaHub = anna.Hub;

        // Relay and acknowledgement: Anna's update gets sequence 1, Boris receives it once; the same id again is the same update.
        var id = Guid.NewGuid().ToString("D");
        var typed = anna.Edit("\"Text A.\"", "\"Text A. Anna.\"");
        Assert.Equal(1, await anna.SendAsync(typed, id));
        await CollaborationContentGuardHttpTests.WaitUntilAsync(() => boris.Received.Count == 1);
        // A retry of the same id (a lost acknowledgement) is the same update: its sequence, one stored update; peers may get it again
        // (the hub relays a retry for whoever missed it) and Yjs applies it once.
        Assert.Equal(1, await anna.Hub.InvokeAsync<long>("SendUpdate", anna.DocumentId, id, typed));
        await Task.Delay(300);
        Assert.Equal([1L], boris.Received.Distinct());
        Assert.Equal(1L, await f.Site.ScalarAsync("""SELECT count(*) FROM "CollaborationUpdate" WHERE "PageId" = @page""", ("page", Guid.Parse(page.Id))));
        Assert.Equal(anna.Document, boris.Document);
        Assert.Single(boris.Document.Split("Anna.")[1..]);
        boris.Received.Clear();

        // Boris drops out, Anna types twice, Boris comes back on a new connection and catches up from the server.
        await boris.Hub.DisposeAsync();
        Assert.Equal(2, await anna.SendAsync(anna.Edit("\"Text A. Anna.\"", "\"Text A. Anna. Podruhé.\"")));
        Assert.Equal(3, await anna.SendAsync(anna.Edit("\"Text A. Anna. Podruhé.\"", "\"Text A. Anna. Podruhé. Potřetí.\"")));
        boris = await OpenAsync(f, space, "boris", page.Id, 203, joined: false);
        await using var borisHub = boris.Hub;
        Assert.Equal(3, boris.Joined.Seq);
        Assert.Equal(anna.Document, boris.Document);

        // An agent rewrites section C while Boris, who has not seen it yet, types into the same paragraph and Anna into section A.
        var borisInC = boris.Edit("\"Text C.\"", "\"Text C. Boris.\"");
        var annaInA = anna.Edit("Potřetí.\"", "Potřetí. Počtvrté.\"");
        var (tokenId, client) = await McpAttachmentToolTests.DraftClientAsync(f);
        var written = await CallAsync(client, "atlas_update_section", new
        {
            pageId = page.Id, expectedVersion = await VersionAsync(f, page.Id), headingId = "c", markdown = "## C\n\nText C přepsal agent.\n"
        });
        Assert.True(written.IsError != true, Text(written));
        await CollaborationContentGuardHttpTests.WaitUntilAsync(() => anna.Received.Count == 1 && boris.Received.Count == 1);
        Assert.Contains(anna.Presence, presence => presence.Contains("· AI (MCP: Claude Code)", StringComparison.Ordinal));
        await anna.SendAsync(annaInA);
        await boris.SendAsync(borisInC);
        await CollaborationContentGuardHttpTests.WaitUntilAsync(() => anna.Received.Count == 2 && boris.Received.Count == 2);
        var third = await OpenAsync(f, space, "anna", page.Id, 303, joined: false);
        await using var thirdHub = third.Hub;
        Assert.Equal(anna.Document, boris.Document);
        Assert.Equal(anna.Document, third.Document);
        foreach (var text in new[] { "Text A. Anna. Podruhé. Potřetí. Počtvrté.", "přepsal agent", "Boris.", "Text B." })
        {
            Assert.Single(anna.Document.Split(text)[1..]);
        }
        // History: the people's edits are their checkpoint (web, Anna typed last), the agent's change a revision of its own (mcp, its token).
        Assert.Equal($"2:web:{anna.UserId}|3:mcp:{f.AdminId}:{tokenId}", await f.Site.ScalarAsync("""
            SELECT string_agg(r."Version" || ':' || r."Channel" || ':' || r."AuthorId" || coalesce(':' || r."ApiTokenId", ''), '|' ORDER BY r."Version")
            FROM "PageRevision" r WHERE r."PageId" = @page AND r."Version" > 1
            """, ("page", Guid.Parse(page.Id))));

        // A second agent write into section B, nobody types there, undone as a whole: B is back, everything else stays, still live.
        var rewrite = await CallAsync(client, "atlas_update_section", new
        {
            pageId = page.Id, expectedVersion = await VersionAsync(f, page.Id), headingId = "b", markdown = "## B\n\nText B přepsal agent.\n"
        });
        Assert.True(rewrite.IsError != true, Text(rewrite));
        await CollaborationContentGuardHttpTests.WaitUntilAsync(() => anna.Document.Contains("Text B přepsal agent.", StringComparison.Ordinal));
        var agentRevision = await f.Site.ScalarAsync("""SELECT "Id" FROM "PageRevision" WHERE "PageId" = @page AND "ActiveTo" IS NULL""", ("page", Guid.Parse(page.Id)));
        var undone = await CallAsync(client, "atlas_undo_change", new { pageId = page.Id, revisionId = agentRevision!.ToString(), expectedVersion = await VersionAsync(f, page.Id) });
        Assert.True(undone.IsError != true, Text(undone));
        await CollaborationContentGuardHttpTests.WaitUntilAsync(() => !anna.Document.Contains("Text B přepsal agent.", StringComparison.Ordinal));
        Assert.Equal(anna.Document, boris.Document);
        Assert.Contains("\"Text B.\"", anna.Document);
        Assert.Contains("Text C přepsal agent. Boris.", anna.Document);
        Assert.Contains("Počtvrté.", anna.Document);
        Assert.Equal(anna.DocumentId, (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(anna.Browser, HttpMethod.Post, $"/api/pages/{page.Id}/collaboration"),
            HttpStatusCode.OK))["documentId"]!.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task Oversized_schema_violating_and_unknown_plugin_input_is_refused_and_the_stored_state_stays()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await SpaceAsync(f);
        var page = await PageAsync(f.Admin, space, "Meze", Rich(Heading(2, "a", "A"), Paragraph("Text A.")), "## A\n\nText A.");
        var anna = await OpenAsync(f, space, "anna", page.Id, 404);
        await using var hub = anna.Hub;
        Assert.Equal(1, await anna.SendAsync(anna.Edit("\"Text A.\"", "\"Text A. Uloženo.\"")));
        var before = await StoredAsync(f, page.Id);
        var document = anna.Document;

        await Assert.ThrowsAnyAsync<HubException>(() => anna.Hub.InvokeAsync<long>("SendUpdate", anna.DocumentId, Guid.NewGuid().ToString("D"),
            new byte[4 * 1024 * 1024 + 1]));
        // A requirement heading in a state outside the plugin's contract.
        var violating = anna.Edit("\"state\":null", "\"state\":\"foo\"", replaceFirstOnly: true);
        var refused = await Assert.ThrowsAnyAsync<HubException>(() => anna.Hub.InvokeAsync<long>("SendUpdate", anna.DocumentId, Guid.NewGuid().ToString("D"), violating));
        Assert.Contains("INVALID_CONTENT", refused.Message);
        var stranger = new MarkdownDocumentSchema(Schema.Version, Schema.Profile, [.. Schema.Plugins, new MarkdownSchemaPlugin("evil-plugin", 1)]);
        await using var other = CollaborationContentGuardHttpTests.Connect(f.Site, anna.Cookies);
        await other.StartAsync();
        await Assert.ThrowsAnyAsync<HubException>(() => other.InvokeAsync<JoinResult>("JoinDocumentWithSchema", anna.DocumentId, stranger));

        Assert.Equal(before, await StoredAsync(f, page.Id));
        var fresh = await OpenAsync(f, space, "anna", page.Id, 405, joined: false);
        await using var freshHub = fresh.Hub;
        Assert.Equal(document, fresh.Document);
        Assert.Equal(1, fresh.Joined.Seq);
    }

    [PostgreSqlFact]
    public async Task An_editor_holding_a_replaced_generation_is_refused_and_the_new_one_is_the_servers_seed_of_the_new_revision()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await SpaceAsync(f);
        var page = await PageAsync(f.Admin, space, "Generace", Rich(Paragraph("Starý text.")), "Starý text.");
        var anna = await OpenAsync(f, space, "anna", page.Id, 505);
        var stale = anna.DocumentId;
        await anna.Hub.DisposeAsync();

        // A save outside a live session: a new generation, whose live state the server stores after the commit (outbox).
        var view = await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/pages/{page.Id}"), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/pages/{page.Id}", new
        {
            title = "Generace", contentJson = Rich(Paragraph("Nový text.")), markdown = (string?)null, expectedVersion = view["version"]!.GetValue<int>(),
        }), HttpStatusCode.OK);
        await CollaborationContentGuardHttpTests.WaitUntilAsync(() => f.Site.ScalarAsync("""
            SELECT "StateSha256" IS NOT NULL AND strpos("ProjectionJson", 'Nový text.') > 0 FROM "PageCollaboration" WHERE "PageId" = @page
            """, ("page", Guid.Parse(page.Id))).GetAwaiter().GetResult() is true);

        await using var old = CollaborationContentGuardHttpTests.Connect(f.Site, anna.Cookies);
        await old.StartAsync();
        await Assert.ThrowsAnyAsync<HubException>(() => old.InvokeAsync<JoinResult>("JoinDocumentWithSchema", stale, Schema));
        var renewed = await OpenAsync(f, space, "anna", page.Id, 506, joined: false);
        await using var hub = renewed.Hub;
        Assert.NotEqual(stale, renewed.DocumentId);
        Assert.Contains("Nový text.", renewed.Document);
        Assert.DoesNotContain("Starý text.", renewed.Document);
    }

    /// <summary>
    /// atlas-requirements 3 → 4 in a live document: a state a 3.6 editor before version 4 left unsaved (bound to the previous host schema, its
    /// requirement naming the party in its facts line) is opened after the upgrade. The open moves it to a new generation and the server
    /// moves the party into the heading as one live write of the system, so the live state reads as version 4 (party in the heading,
    /// the deadline line kept) before any editor loads it; a second open changes nothing.
    /// </summary>
    [PostgreSqlFact]
    public async Task A_live_version_3_state_opened_after_the_upgrade_reads_as_version_4_and_a_second_open_changes_nothing()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await SpaceAsync(f);
        var page = await PageAsync(f.Admin, space, "Požadavky", Rich(Heading(2, "r1", "Certifikát"), Paragraph("Poznámka.")), "## Certifikát\n\nPoznámka.");
        var anna = await OpenAsync(f, space, "anna", page.Id, 707);
        await anna.Hub.DisposeAsync();
        // What a version-3 editor typed and left unsaved: the heading a requirement, its facts line „od ACME · do 15. 11. 2026“.
        var v3 = anna.Edit(document => document
            .Replace("\"state\":null", "\"state\":\"planned\"", StringComparison.Ordinal)
            .Replace("{\"type\":\"paragraph\"", "{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"od ACME · do 15. 11. 2026\"}]},{\"type\":\"paragraph\"",
                StringComparison.Ordinal));
        await new PostgresCollaborationStore(f.Site.Services.GetRequiredService<IServiceScopeFactory>())
            .AppendUpdateAsync(anna.DocumentId, Guid.NewGuid(), anna.UserId, DateTimeOffset.UtcNow, v3, new UpdateOrigin("web"), CancellationToken.None);
        await f.Site.ScalarAsync("""UPDATE "PageCollaboration" SET "SchemaJson" = @schema WHERE "PageId" = @page""",
            ("schema", JsonSerializer.Serialize(CollaborationSchema.KnownHostSchemas[^2])), ("page", Guid.Parse(page.Id)));

        var opened = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(anna.Browser, HttpMethod.Post, $"/api/pages/{page.Id}/collaboration"), HttpStatusCode.OK);

        Assert.NotEqual(anna.DocumentId, opened["documentId"]!.GetValue<string>());
        var live = await LiveAsync(f, page.Id);
        var heading = live["content"]![0]!;
        Assert.Equal(("planned", "ACME"), (heading["attrs"]!["state"]!.GetValue<string>(), heading["attrs"]!["party"]!.GetValue<string>()));
        Assert.Equal("do 15. 11. 2026", live["content"]![1]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.DoesNotContain("od ACME", live.ToJsonString());
        Assert.Equal("2:system", await f.Site.ScalarAsync("""
            SELECT count(*) || ':' || max(CASE WHEN u."Sequence" = (SELECT max("Sequence") FROM "CollaborationUpdate" WHERE "PageId" = @page) THEN u."Channel" END)
            FROM "CollaborationUpdate" u WHERE u."PageId" = @page
            """, ("page", Guid.Parse(page.Id))));

        var again = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(anna.Browser, HttpMethod.Post, $"/api/pages/{page.Id}/collaboration"), HttpStatusCode.OK);
        Assert.Equal(opened["documentId"]!.GetValue<string>(), again["documentId"]!.GetValue<string>());
        Assert.Equal(2L, await f.Site.ScalarAsync("""SELECT count(*) FROM "CollaborationUpdate" WHERE "PageId" = @page""", ("page", Guid.Parse(page.Id))));
        Assert.Equal(live.ToJsonString(), (await LiveAsync(f, page.Id)).ToJsonString());
    }

    /// <summary>The page's live document as the stored state and log show it.</summary>
    private static async Task<JsonNode> LiveAsync(McpSite f, string pageId)
    {
        var state = (byte[])(await f.Site.ScalarAsync("""SELECT "State" FROM "PageCollaboration" WHERE "PageId" = @page""", ("page", Guid.Parse(pageId))))!;
        var updates = new List<ReadOnlyMemory<byte>>();
        for (var sequence = 1L; await f.Site.ScalarAsync("""
                SELECT u."Payload" FROM "CollaborationUpdate" u JOIN "PageCollaboration" c ON c."PageId" = u."PageId" AND c."Generation" = u."Generation"
                WHERE u."PageId" = @page AND u."Sequence" = @sequence
                """, ("page", Guid.Parse(pageId)), ("sequence", sequence)) is byte[] payload; sequence++)
        {
            updates.Add(payload);
        }
        return JsonNode.Parse(Host.Materialize(state, updates))!;
    }

    private static async Task<string> SpaceAsync(McpSite f)
    {
        var space = await f.FirstSpaceAsync();
        foreach (var name in new[] { "anna", "boris" })
        {
            var (_, user) = await f.UserAsync(name, level: null);
            await f.GrantAsync(space, user, "Writer");
        }
        return space;
    }

    private static async Task<int> VersionAsync(McpSite f, string pageId)
        => (int)(await f.Site.ScalarAsync("""SELECT "Version" FROM "Page" WHERE "Id" = @page""", ("page", Guid.Parse(pageId))))!;

    private static async Task<string> StoredAsync(McpSite f, string pageId)
        => (string)(await f.Site.ScalarAsync("""
            SELECT c."Generation" || ':' || c."Sequence" || ':' || coalesce(c."StateSha256", '-') || ':' || (SELECT count(*) FROM "CollaborationUpdate" u WHERE u."PageId" = c."PageId")
            FROM "PageCollaboration" c WHERE c."PageId" = @page
            """, ("page", Guid.Parse(pageId))))!;

    /// <summary>
    /// A person opens the page as the app does: the bundle (which must carry the server's seed), the collaboration lease, the hub join with the
    /// editor's schema. The join must hydrate from the stored state, never seed. <paramref name="joined"/> false: someone joined it before,
    /// so the bundle's state may be older than the join's.
    /// </summary>
    private static async Task<Editor> OpenAsync(McpSite f, string space, string name, string pageId, uint client, bool joined = true)
    {
        // The writer of the test's space with that display name (SpaceAsync); its login is unique on the shared site.
        var userId = (await f.Site.ScalarAsync("""
            SELECT u."Id" FROM "UserAccount" u JOIN "SpaceGrant" g ON g."UserId" = u."Id" AND g."ActiveTo" IS NULL
            WHERE g."SpaceId" = @space::uuid AND u."DisplayName" = @name
            """, ("space", space), ("name", name)))!.ToString()!;
        var username = f.Login(userId);
        var cookies = new CookieContainer();
        var browser = f.Site.CreateDefaultClient(CollaborationContentGuardHttpTests.BaseAddress, new CookieContainerHandler(cookies));
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(browser, HttpMethod.Post, "/api/auth/login", new { username, password = f.Site.Password }),
            HttpStatusCode.OK);
        var bundle = await AtlasHttpSite.Json(await browser.GetAsync($"/api/pages/{pageId}?bundle=true&visit=true"), HttpStatusCode.OK);
        var bundled = await LiveStateAsync(browser, pageId, bundle["bundle"]!["collaboration"]!["generation"]!.GetValue<string>());
        var documentId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(browser, HttpMethod.Post, $"/api/pages/{pageId}/collaboration"), HttpStatusCode.OK))
            ["documentId"]!.GetValue<string>();
        var editor = new Editor(CollaborationContentGuardHttpTests.Connect(f.Site, cookies), cookies, browser, documentId, client,
            userId);
        editor.Hub.On<string, byte[], long>("ReceiveUpdate", (_, update, sequence) => editor.Receive(update, sequence));
        editor.Hub.On<string, byte[]>("ReceiveAwareness", (_, payload) => { lock (editor.Presence) editor.Presence.Add(Encoding.UTF8.GetString(payload)); });
        await editor.Hub.StartAsync();
        var join = await editor.JoinAsync();
        // A join that seeds gets no snapshot (the editor would seed it from the page); this one hydrates from the server's seed.
        Assert.NotNull(join.Snapshot);
        Assert.True(join.CanWrite);
        if (joined)
        {
            Assert.Equal(bundled, join.Snapshot);
        }
        return editor;
    }

    /// <summary>The bundle's live state as the app reads it beside the bundle: gzipped, tagged with its generation, kept by the browser only
    /// privately and asked again by its ETag, which an unchanged state answers with 304.</summary>
    private static async Task<byte[]> LiveStateAsync(HttpClient browser, string pageId, string generation)
    {
        using var response = await browser.GetAsync($"/api/pages/{pageId}/collaboration/state");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl is { Private: true, NoCache: true, NoStore: false, Public: false });
        var tag = response.Headers.ETag!;
        Assert.StartsWith($"\"{generation}.", tag.Tag, StringComparison.Ordinal);
        using var again = new HttpRequestMessage(HttpMethod.Get, $"/api/pages/{pageId}/collaboration/state");
        again.Headers.IfNoneMatch.Add(tag);
        using var unchanged = await browser.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        using var gzip = new GZipStream(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using var state = new MemoryStream();
        await gzip.CopyToAsync(state);
        return state.ToArray();
    }

    /// <summary>An editor's connection and its replica: the joined state and every update it sent or received, in order.</summary>
    private sealed class Editor(HubConnection hub, CookieContainer cookies, HttpClient browser, string documentId, uint client, string userId)
    {
        private readonly List<byte[]> m_updates = [];
        private byte[] m_state = [];

        public HubConnection Hub { get; } = hub;
        public CookieContainer Cookies { get; } = cookies;
        public HttpClient Browser { get; } = browser;
        public string DocumentId { get; } = documentId;
        public string UserId { get; } = userId;
        public List<long> Received { get; } = [];
        public List<string> Presence { get; } = [];
        public JoinResult Joined { get; private set; } = null!;

        public string Document
        {
            get { lock (m_updates) return Host.Materialize(m_state, m_updates.Select(update => (ReadOnlyMemory<byte>)update).ToArray()); }
        }

        public async Task<JoinResult> JoinAsync()
        {
            Joined = await Hub.InvokeAsync<JoinResult>("JoinDocumentWithSchema", DocumentId, Schema);
            lock (m_updates)
            {
                m_state = Joined.Snapshot ?? [];
                m_updates.AddRange(Joined.Updates);
            }
            return Joined;
        }

        public void Receive(byte[] update, long sequence)
        {
            lock (m_updates)
            {
                m_updates.Add(update);
                Received.Add(sequence);
            }
        }

        /// <summary>What this editor types: the update that turns its replica into its document with <paramref name="from"/> replaced.</summary>
        public byte[] Edit(string from, string to, bool replaceFirstOnly = false) => Edit(document =>
        {
            var at = document.IndexOf(from, StringComparison.Ordinal);
            Assert.True(at >= 0, $"'{from}' is not in {document}");
            return replaceFirstOnly ? document[..at] + to + document[(at + from.Length)..] : document.Replace(from, to, StringComparison.Ordinal);
        });

        /// <summary>What this editor types: the update that turns its replica into <paramref name="edit"/> of its document's JSON.</summary>
        public byte[] Edit(Func<string, string> edit)
        {
            var target = edit(Document);
            using var json = JsonDocument.Parse(target);
            lock (m_updates)
            {
                return Writer.Write(m_state, m_updates.Select(update => (ReadOnlyMemory<byte>)update).ToArray(), json.RootElement, client).Update;
            }
        }

        public async Task<long> SendAsync(byte[] update, string? id = null)
        {
            lock (m_updates) m_updates.Add(update);
            return await Hub.InvokeAsync<long>("SendUpdate", DocumentId, id ?? Guid.NewGuid().ToString("D"), update);
        }
    }
}
