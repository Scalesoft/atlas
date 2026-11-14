using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Platform.Validation;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>
/// The WP3 Yjs boundary through Atlas's real host: cookie login, OpenCollaboration, the /hubs/markdown hub with the
/// managed content guard registered by CollaborationModule, and the PostgreSQL store behind it.
/// </summary>
public sealed class CollaborationContentGuardHttpTests
{
    internal const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
    internal static readonly Uri BaseAddress = new("https://localhost");

    // Payloads of the editor's Yjs corpus (Atlas.Markdown.Sync.Tests/Fixtures/yjs-corpus/hostile in the editor repository).
    // 000-baseline.state.bin: a y-prosemirror document (paragraph, table with chart references).
    internal static readonly byte[] Baseline = Convert.FromBase64String(
        "AR6bociVDwAHAQdkZWZhdWx0AwlwYXJhZ3JhcGgHAJuhyJUPAAYEAJuhyJUPAQtIZWxsbyB3b3JsZIebociVDwADBXRhYmxlBwCbociVDw0DCHRhYmxlUm93BwCbociVDw4DCXRhYmxlQ2VsbAcAm6HIlQ8PAwlwYXJhZ3JhcGgoAJuhyJUPDwhjaGFydFJlZgF3KmNoYXJ0LWViOWI0OTg2LTgxY2EtNDQxOS05NjE2LWFmZGQyNWE2ZmQyNygAm6HIlQ8PB2NvbHNwYW4BfQEoAJuhyJUPDwdyb3dzcGFuAX0Bh5uhyJUPDwMJdGFibGVDZWxsBwCbociVDxQDCXBhcmFncmFwaCgAm6HIlQ8UCGNoYXJ0UmVmAXcqY2hhcnQtOTc2NzMwMzMtZjZiYy00ZWRkLTk3N2YtNTFlNDRkNWU5NGM5KACbociVDxQHY29sc3BhbgF9ASgAm6HIlQ8UB3Jvd3NwYW4BfQEoAJuhyJUPDghjaGFydFJlZgF3KmNoYXJ0LWMxNzJkMjVjLTU1MWQtNGVmMC04MTRhLWE1YzUxZDIxMDhjY4ebociVDw4DCHRhYmxlUm93BwCbociVDxoDCXRhYmxlQ2VsbAcAm6HIlQ8bAwlwYXJhZ3JhcGgoAJuhyJUPGwhjaGFydFJlZgF3KmNoYXJ0LThiZTNhNzVjLWE4OTEtNDI1MC04MjVkLTk3MDE3NGJhZGUzNCgAm6HIlQ8bB2NvbHNwYW4BfQEoAJuhyJUPGwdyb3dzcGFuAX0Bh5uhyJUPGwMJdGFibGVDZWxsBwCbociVDyADCXBhcmFncmFwaCgAm6HIlQ8gCGNoYXJ0UmVmAXcqY2hhcnQtODVhODk3ZDgtOTU5NC00ZGJkLWI4YTAtMDAyMmI1MzIwNTcyKACbociVDyAHY29sc3BhbgF9ASgAm6HIlQ8gB3Jvd3NwYW4BfQEoAJuhyJUPGghjaGFydFJlZgF3KmNoYXJ0LTM3YzQwZDAyLTJlNTYtNDVlZi1iODRmLWFkYTI0ODk0YzBjOSgAm6HIlQ8NCGNoYXJ0UmVmAXcqY2hhcnQtZDNjNmVmNzgtNzMzYS00NTUyLWEzMTItYjk1MThiZTYzMzU0h5uhyJUPDQMJcGFyYWdyYXBoAA==");

    // 014-legit_type_control_.update.bin: an honest edit, text typed into the first paragraph.
    internal static readonly byte[] HonestEdit = Convert.FromBase64String("AQGUttXWBgDEm6HIlQ8Gm6HIlQ8HBiB0aGVyZQA=");

    // 003-raw_secretReference_block.update.bin: a crafted update inserting a secretReference block with its attributes.
    private static readonly byte[] SecretNode = Convert.FromBase64String(
        "AQew6cHoAQDHm6HIlQ8Am6HIlQ8NAw9zZWNyZXRSZWZlcmVuY2UoALDpwegBAAhzZWNyZXRJZAF3JGQ4NDdiM2FmLTRhODctNGU5Ny1hYWI4LWE3NGE0Y2Y0OGM4OSgAsOnB6AEABWZpZWxkAXcIcGFzc3dvcmQoALDpwegBAAZmaWVsZHMBdQN3CHVzZXJuYW1ldwhwYXNzd29yZHcDdXJsKACw6cHoAQAIc2hvd0NvcHkBeCgAsOnB6AEAC3Nob3dEZXRhaWxzAXgoALDpwegBAA9hdXRvSGlkZVNlY29uZHMBfTwA");

    // Built with yjs 13.6 on Baseline (client 4242): a requirement block of Atlas's own plugin atlas-requirements (3.5, retired in 3.6)
    // appended to the document, with every attribute set.
    private static readonly byte[] RequirementBlock = Convert.FromBase64String(
        "AQySIQCHm6HIlQ8nAwtyZXF1aXJlbWVudAcAkiEAAwlwYXJhZ3JhcGgHAJIhAQYEAJIhAg9TU0wgY2VydGlmaWvDoXQoAJIhAAJpZAF3JDZmMWMxZDNlLThhNGItNGM1NS05ZDFlLTAwMDAwMDAwMDAwMigAkiEAA2tleQF3CnBvcnRhbC5zc2woAJIhAAlkaXJlY3Rpb24BdwdpbmJvdW5kKACSIQAFc3RhdGUBdwRkb25lKACSIQAEZGF0ZQF3CjIwMjYtMDktMTAoAJIhAAVwYXJ0eQF3BklUIEtLTigAkiEABXdoZXJlAXcHS2VlcGFzcygAkiEACHJlcXVpcmVzAXcAAA==");

    /// <summary>A 3.5 tab still bound to a 3.5 generation (reconnected after the upgrade) can no longer insert the retired requirement
    /// block; a live document that holds one (stored before the content repair moved it) still opens and takes edits.</summary>
    [PostgreSqlFact]
    public async Task A_retired_requirement_block_is_read_from_the_seed_but_no_update_may_insert_one()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_collab_requirement_", configureServices: BrowserSeeds);
        var cookies = new CookieContainer();
        using var admin = site.CreateDefaultClient(BaseAddress, new CookieContainerHandler(cookies));
        await site.CompleteSetupAsync(admin);
        var spaces = await AtlasHttpSite.Json(await admin.GetAsync("/api/spaces"), HttpStatusCode.OK);
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId = spaces[0]!["id"]!.GetValue<string>(), parentId = (string?)null, title = "KBN", contentJson = Rich, markdown = "" }), HttpStatusCode.OK);
        var session = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{page["id"]!.GetValue<string>()}/collaboration"), HttpStatusCode.OK);
        var documentId = session["documentId"]!.GetValue<string>();
        await using var writer = Connect(site, cookies);
        await writer.StartAsync();
        Assert.True((await JoinAsync(site, writer, documentId)).IsSnapshotter);
        var seed = Atlas.Yjs.YjsUpdateMerger.Merge([Baseline, RequirementBlock], 1 << 20);
        await writer.InvokeAsync("PushSnapshot", documentId, 0L, seed, Projections(0, seed));

        // Another requirement block like the stored one, as a client that still has the node would insert it.
        var schema = HostDocumentValidator.Instance.Schema;
        var document = System.Text.Json.Nodes.JsonNode.Parse(new YjsDocumentMaterializer(schema).Materialize(seed))!.AsObject();
        var blocks = document["content"]!.AsArray();
        blocks.Add(blocks.Single(block => block!["type"]!.GetValue<string>() == "requirement")!.DeepClone());
        using var target = JsonDocument.Parse(document.ToJsonString());
        var insert = new YjsDocumentWriter(schema).Write(seed, [], target.RootElement).Update;
        var error = await Assert.ThrowsAsync<HubException>(() => writer.InvokeAsync<long>("SendUpdate", documentId, Guid.NewGuid().ToString("D"), insert));
        Assert.Contains("[INVALID_CONTENT] The document does not accept this content (UnknownNode)", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, await writer.InvokeAsync<long>("SendUpdate", documentId, Guid.NewGuid().ToString("D"), HonestEdit));
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task An_honest_client_edits_while_a_secret_node_update_is_refused_as_invalid_content()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_collab_guard_", configureServices: BrowserSeeds);
        var cookies = new CookieContainer();
        using var admin = site.CreateDefaultClient(BaseAddress, new CookieContainerHandler(cookies));
        await site.CompleteSetupAsync(admin);
        var spaces = await AtlasHttpSite.Json(await admin.GetAsync("/api/spaces"), HttpStatusCode.OK);
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId = spaces[0]!["id"]!.GetValue<string>(), parentId = (string?)null, title = "Guarded", contentJson = Rich, markdown = "" }), HttpStatusCode.OK);
        var pageId = Guid.Parse(page["id"]!.GetValue<string>());
        var session = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{pageId}/collaboration"), HttpStatusCode.OK);
        var documentId = session["documentId"]!.GetValue<string>();

        await using var honest = Connect(site, cookies);
        await using var peer = Connect(site, cookies);
        await using var malicious = Connect(site, cookies);
        await honest.StartAsync();
        await peer.StartAsync();
        await malicious.StartAsync();
        var received = new List<byte[]>();
        peer.On<string, byte[], long>("ReceiveUpdate", (_, update, _) => { lock (received) received.Add(update); });

        // The first writer seeds the document; the guard admits the self-contained editor state.
        Assert.True((await JoinAsync(site, honest, documentId)).IsSnapshotter);
        await honest.InvokeAsync("PushSnapshot", documentId, 0L, Baseline, Projections(0, Baseline));
        await JoinAsync(site, peer, documentId);
        await JoinAsync(site, malicious, documentId);

        Assert.Equal(1, await honest.InvokeAsync<long>("SendUpdate", documentId, Guid.NewGuid().ToString("D"), HonestEdit));

        // The secret it names does not exist, so nobody may Discover it (WP4 V6 SecretReferenceNodeGuard): refused before it is stored or relayed.
        var error = await Assert.ThrowsAsync<HubException>(() =>
            malicious.InvokeAsync<long>("SendUpdate", documentId, Guid.NewGuid().ToString("D"), SecretNode));
        Assert.Contains("[INVALID_CONTENT]", error.Message, StringComparison.Ordinal);

        // The honest client keeps editing on the same document after the refusal.
        Assert.Equal(2, await honest.InvokeAsync<long>("SendUpdate", documentId, Guid.NewGuid().ToString("D"), HonestEdit));
        await WaitUntilAsync(() => { lock (received) return received.Count == 2; });
        lock (received)
        {
            Assert.All(received, update => Assert.Equal(HonestEdit, update));
        }
        var stored = await site.ScalarAsync("SELECT count(*) FROM \"CollaborationUpdate\" WHERE \"PageId\" = @page", ("page", pageId));
        Assert.Equal(2L, stored);
        var secretPayloads = await site.ScalarAsync("SELECT count(*) FROM \"CollaborationUpdate\" WHERE \"PageId\" = @page AND \"Payload\" = @payload",
            ("page", pageId), ("payload", SecretNode));
        Assert.Equal(0L, secretPayloads);
    }

    /// <summary>Review T13: the library answers an authorization that outlasts its timeout (5 s) as "[FORBIDDEN] Document access denied"
    /// and only its log says why. This documents that behaviour with a lookup slower than the timeout, which is shortened here, not
    /// raised; the retriable answer belongs to the Sync takeover.</summary>
    [PostgreSqlFact]
    public async Task A_join_whose_authorization_outlasts_the_timeout_is_refused_and_logged_as_unavailable()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_collab_slow_authorization_", configureServices: services =>
        {
            BrowserSeeds(services);
            services.Replace(ServiceDescriptor.Singleton(new MarkdownSyncOptions { AuthorizationTimeout = TimeSpan.FromMilliseconds(200) }));
            services.Replace(ServiceDescriptor.Scoped<ICollabAuthenticator>(provider =>
                new SlowAuthenticator(ActivatorUtilities.CreateInstance<AtlasCollaborationAuthenticator>(provider))));
        });
        var cookies = new CookieContainer();
        using var admin = site.CreateDefaultClient(BaseAddress, new CookieContainerHandler(cookies));
        await site.CompleteSetupAsync(admin);
        var spaces = await AtlasHttpSite.Json(await admin.GetAsync("/api/spaces"), HttpStatusCode.OK);
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId = spaces[0]!["id"]!.GetValue<string>(), parentId = (string?)null, title = "Pomalá", contentJson = Rich, markdown = "" }), HttpStatusCode.OK);
        var session = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{page["id"]!.GetValue<string>()}/collaboration"), HttpStatusCode.OK);
        await using var writer = Connect(site, cookies);
        await writer.StartAsync();

        var refused = await Assert.ThrowsAsync<HubException>(() => writer.InvokeAsync<JoinResult>("JoinDocument", session["documentId"]!.GetValue<string>()));

        Assert.Contains("[FORBIDDEN]", refused.Message, StringComparison.Ordinal);
        Assert.Contains(site.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Category.EndsWith(nameof(DocumentAccessCoordinator), StringComparison.Ordinal)
            && entry.Message.Contains("authorization unavailable", StringComparison.Ordinal) && entry.Message.Contains("Canceled", StringComparison.Ordinal));
    }

    /// <summary>Atlas's authenticator behind a lookup that waits until it is cancelled.</summary>
    private sealed class SlowAuthenticator(ICollabAuthenticator inner) : ICollabAuthenticator
    {
        public Task<CollabUser?> AuthenticateAsync(Microsoft.AspNetCore.Http.HttpContext handshakeContext, CancellationToken ct) => inner.AuthenticateAsync(handshakeContext, ct);

        public async Task<DocAccess> AuthorizeAsync(CollabUser user, string documentId, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return await inner.AuthorizeAsync(user, documentId, ct);
        }
    }

    /// <summary>JoinDocument; a refusal fails with the server's warnings and errors, so the cause the library logs is visible (T13).</summary>
    internal static async Task<JoinResult> JoinAsync(AtlasHttpSite site, HubConnection hub, string documentId)
    {
        try
        {
            return await hub.InvokeAsync<JoinResult>("JoinDocument", documentId);
        }
        catch (HubException error)
        {
            var log = site.Logs.Entries.Where(entry => entry.Level >= LogLevel.Warning).Select(entry => $"{entry.Level} {entry.Category}: {entry.Message}");
            throw new InvalidOperationException(error.Message + "\nServer warnings and errors:\n" + string.Join("\n", log), error);
        }
    }

    internal static HubConnection Connect(AtlasHttpSite site, CookieContainer cookies)
    {
        var hub = new Uri(BaseAddress, "/hubs/markdown");
        return new HubConnectionBuilder()
            .WithUrl(hub, options =>
            {
                // TestServer has no socket; long polling runs every request through its in-memory handler.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => site.Server.CreateHandler();
                options.Headers["Cookie"] = cookies.GetCookieHeader(hub);
            })
            .Build();
    }

    internal static ProjectionSetDto Projections(long sequence, byte[] state)
    {
        using var json = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\"}]}");
        return new ProjectionSetDto(sequence, Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant(), "", json.RootElement.Clone(), "");
    }

    /// <summary>For a test whose browser seeds the page (the fallback when the server's seed fails, 3.6 LiveSeeds): the managed authority
    /// without its seed, so the server's seed of a new page never races the test's own.</summary>
    internal static void BrowserSeeds(IServiceCollection services) => services.AddSingleton<ILiveWriteAuthority, BrowserSeedAuthority>();

    private sealed class BrowserSeedAuthority : ILiveWriteAuthority
    {
        private readonly ManagedLiveWriteAuthority m_inner = new();

        public Task<DocSnapshot> CheckpointAsync(MarkdownDocumentSchema schema, byte[] snapshot, IReadOnlyList<byte[]> updates, long sequence, CancellationToken ct)
            => m_inner.CheckpointAsync(schema, snapshot, updates, sequence, ct);

        public Task<byte[]> WriteAsync(MarkdownDocumentSchema schema, byte[] snapshot, IReadOnlyList<byte[]> updates, JsonElement target, long sequence, CancellationToken ct)
            => m_inner.WriteAsync(schema, snapshot, updates, target, sequence, ct);

        public Task<DocSnapshot> SeedAsync(MarkdownDocumentSchema schema, JsonElement document, CancellationToken ct)
            => throw new InvalidOperationException("The test's browser seeds this page.");
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.True(condition(), "condition not met within timeout");
    }
}
