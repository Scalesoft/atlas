using System.Security.Cryptography;
using System.Text.Json;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Collaboration.Contracts;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.PageLinks.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Tests.Projector;
using Atlas.Server.Tests.Vaults;
using Xunit;

namespace Atlas.Server.Tests.Pages;

/// <summary>3.6: the page bundle names the live session, and its state read serves the Yjs state, exactly when the hub would serve it
/// under the session's document id.</summary>
public sealed class PageBundleCollaborationTests
{
    [PostgreSqlFact]
    public async Task The_bundle_carries_the_stored_live_state_only_when_the_session_would_open_it_as_is()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_bundle_collab_");
        var writer = await host.SeedUserAsync("Writer");
        var reader = await host.SeedUserAsync("Reader");
        var space = await host.SeedSpaceAsync("Provoz", (writer, SpaceRole.Writer), (reader, SpaceRole.Reader));
        host.Actor.Current = writer;
        var rich = ProjectorDocuments.Json(ProjectorDocuments.Paragraph("Postup"));
        var page = (await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(space, null, "Postup", rich, null)))).Id;
        await host.InvokeAsync<PageDto>(new PublishPage(page, new PublishPageRequest(1)));
        Assert.Null(await BundleAsync(host, writer, page));

        var store = new TestCollaboration(host);
        var session = await store.OpenAsync(page, writer);
        // A row without a stored snapshot: the hub would seed it, nothing to bundle.
        Assert.Null(await BundleAsync(host, writer, page));
        var (snapshot, pending) = await StoreAsync(store, session.DocumentId, writer);

        var bundled = await BundleAsync(host, writer, page);
        Assert.NotNull(bundled);
        Assert.Equal((session.DocumentId, session.Generation, session.CurrentVersion, false),
            (bundled.DocumentId, bundled.Generation, bundled.CurrentVersion, bundled.ReadOnly));
        Assert.Equal(YjsUpdateMerger.Merge([snapshot, pending], 4 * 1024 * 1024), await StateAsync(host, writer, page, session.Generation));
        Assert.Equal(session.Generation, (await store.OpenAsync(page, writer)).Generation);

        // A reader of a page that is not live never gets the state: it holds the unpublished working content.
        Assert.Null(await BundleAsync(host, reader, page));
        Assert.Null(await StateAsync(host, reader, page, session.Generation));
        host.Actor.Current = writer;
        await host.InvokeAsync<PagePublicationState>(new SetPagePublication(page, new("Live")));
        var watched = await BundleAsync(host, reader, page);
        Assert.NotNull(watched);
        Assert.Equal((session.DocumentId, true), (watched.DocumentId, watched.ReadOnly));
        Assert.Equal(await StateAsync(host, writer, page, session.Generation), await StateAsync(host, reader, page, session.Generation));

        // Bound to an older host schema, OpenCollaboration would move the state to a new generation.
        await host.SeedAsync(s => s.CreateSQLQuery("""UPDATE "PageCollaboration" SET "SchemaBound" = true, "SchemaJson" = :schema WHERE "PageId" = :page""")
            .SetParameter("schema", JsonSerializer.Serialize(CollaborationSchema.KnownHostSchemas[0])).SetParameter("page", page).ExecuteUpdateAsync());
        Assert.Null(await BundleAsync(host, writer, page));
        Assert.Null(await StateAsync(host, writer, page, session.Generation));
        // Everything saved and the projection differs from the revision: OpenCollaboration would reset it to the revision.
        await host.SeedAsync(s => s.CreateSQLQuery("""UPDATE "PageCollaboration" SET "SchemaBound" = false, "SchemaJson" = NULL, "LastSavedSequence" = "Sequence" WHERE "PageId" = :page""")
            .SetParameter("page", page).ExecuteUpdateAsync());
        Assert.Null(await BundleAsync(host, writer, page));
        // The bundle is a pure read: neither case was reset or upgraded by it.
        Assert.Equal(session.Generation, (await BundleAsync(host, reader, page))!.Generation);
    }

    [PostgreSqlFact]
    public async Task A_live_state_with_a_hidden_secret_reference_stays_out_of_the_bundle()
    {
        await using var f = await VaultFixture.CreateAsync();
        var s = await SecretReferenceGuardTests.SetUpAsync(f);
        await StoreAsync(new TestCollaboration(f.Host), s.DocumentId, s.Writer);
        Assert.NotNull(await BundleAsync(f.Host, s.Stranger, s.PageId));
        await f.Host.SeedAsync(session => session.CreateSQLQuery("""
            INSERT INTO "CollaborationSecretReference"("PageId", "Generation", "SecretId") VALUES (:page, :generation, :secret)
            """).SetParameter("page", s.PageId).SetParameter("generation", s.Generation).SetParameter("secret", s.Router).ExecuteUpdateAsync());

        Assert.NotNull(await BundleAsync(f.Host, s.Writer, s.PageId));
        Assert.Null(await BundleAsync(f.Host, s.Stranger, s.PageId));
        Assert.Null(await StateAsync(f.Host, s.Stranger, s.PageId, s.Generation));
    }

    private static async Task<PageBundleCollaboration?> BundleAsync(AtlasTestHost host, UserAccount actor, Guid page)
    {
        host.Actor.Current = actor;
        return (await host.InvokeAsync<PageView>(new GetPageWithBundle(page, new GetPageConnections(page, 10), true))).Bundle!.Collaboration;
    }

    /// <summary>The state read's merged Yjs state, or null when it serves none; when served, of <paramref name="generation"/>.</summary>
    private static async Task<byte[]?> StateAsync(AtlasTestHost host, UserAccount actor, Guid page, Guid generation)
    {
        host.Actor.Current = actor;
        var live = (await host.InvokeAsync<PageLiveStateRead>(new GetPageLiveState(page))).Live;
        Assert.True(live is null || live.Generation == generation);
        return live?.State;
    }

    /// <summary>A snapshot of the first update, then a second update still pending in the log.</summary>
    private static async Task<(byte[] Snapshot, byte[] Pending)> StoreAsync(TestCollaboration store, string documentId, UserAccount author)
    {
        var first = Insert(0, "a");
        var second = Insert(1, "b");
        await store.AppendUpdateAsync(documentId, Guid.NewGuid(), author.Id.ToString(), DateTimeOffset.UtcNow, first);
        using var doc = JsonDocument.Parse("""{"type":"doc","content":[{"type":"paragraph"}]}""");
        var hash = Convert.ToHexString(SHA256.HashData(first)).ToLowerInvariant();
        await store.FlushAsync(documentId, new(first, 1, new(1, hash, "Snapshot", doc.RootElement.Clone(), "Snapshot")));
        await store.AppendUpdateAsync(documentId, Guid.NewGuid(), author.Id.ToString(), DateTimeOffset.UtcNow, second);
        return (first, second);
    }

    /// <summary>A Yjs v1 update of client 7: one string item in root text "t", at clock 0 or right after the previous one.</summary>
    private static byte[] Insert(byte clock, string text)
    {
        byte[] header = [1, 1, 7, clock];
        byte[] position = clock == 0 ? [0x04, 1, 1, (byte)'t'] : [0x84, 7, (byte)(clock - 1)];
        return [.. header, .. position, (byte)text.Length, .. System.Text.Encoding.ASCII.GetBytes(text), 0];
    }
}
