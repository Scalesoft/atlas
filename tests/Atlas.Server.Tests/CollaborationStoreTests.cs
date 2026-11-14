using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NHibernate;
using Atlas.Markdown.Sync;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Tests.Collaboration;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class CollaborationStoreTests
{
    private static readonly MarkdownDocumentSchema CurrentSchema = CollaborationSchema.KnownHostSchemas[^1];

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task OpenApi_page_seeds_and_flushes_its_projection_byte_identical()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var rich = OpenApiDocuments.Rich();
        var page = await f.Pages.CreateAsync(new(f.Page.SpaceId, null, "API", rich, OpenApiDocuments.Markdown), f.Writer, default);
        var opened = await f.Store.OpenAsync(page.Id, f.Writer);
        await f.Store.BindSchemaAsync(opened.DocumentId, CurrentSchema, default);
        var seeded = (await f.Store.LoadAsync(opened.DocumentId, default))!;
        Assert.Equal(OpenApiDocuments.Projection(rich).GetRawText(), seeded.SeedEditorJson!.Value.GetRawText());

        await f.Append(opened.DocumentId, Guid.NewGuid(), [1, 2, 3]);
        byte[] state = [7, 8];
        var snapshot = new DocSnapshot(state, 1, new(1, Convert.ToHexStringLower(SHA256.HashData(state)),
            OpenApiDocuments.Markdown, OpenApiDocuments.Projection(rich), "Popis API"));
        await f.Store.FlushAsync(opened.DocumentId, snapshot, default);
        Assert.Equal(new byte[] { 7, 8 }, (await f.Store.LoadAsync(opened.DocumentId, default))!.Snapshot);
    }

    [PostgreSqlFact]
    public async Task Older_schema_upgrades_to_the_current_schema_preserves_pending_updates_and_accepts_current_editor_only()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var old = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        var previous = new MarkdownDocumentSchema(2, "rich", [new("attachments", 1), new("heading-anchors", 1), new("youtrack", 1)]);
        await f.Store.BindSchemaAsync(old.DocumentId, previous, default);
        await f.Append(old.DocumentId, Guid.NewGuid(), [1, 2, 3]);
        await f.Store.FlushAsync(old.DocumentId, CollaborationFixture.Snapshot(1), default);
        var pending = Guid.NewGuid();
        await f.Append(old.DocumentId, pending, [4, 5, 6]);
        var before = await CapturePersistedAsync(f, true);

        var opened = await Task.WhenAll(f.Store.OpenAsync(f.Page.Id, f.Writer), f.Store.OpenAsync(f.Page.Id, f.Writer));
        Assert.NotEqual(old.Generation, opened[0].Generation);
        Assert.Equal(opened[0].Generation, opened[1].Generation);
        Assert.Equal(before, await CapturePersistedAsync(f, true));
        var schema = await f.Store.BindSchemaAsync(opened[0].DocumentId, CurrentSchema, default);
        Assert.Equal(CurrentSchema.Plugins, schema!.Plugins);
        await Assert.ThrowsAsync<DocumentSchemaException>(() => f.Store.BindSchemaAsync(opened[0].DocumentId, previous, default));
        await Assert.ThrowsAsync<DocumentSupersededException>(() => f.Append(old.DocumentId, Guid.NewGuid(), [9]));
        Assert.True((await f.Append(opened[0].DocumentId, pending, [4, 5, 6])).Duplicate);
        Assert.Equal(opened[0].Generation, (await f.Store.OpenAsync(f.Page.Id, f.Writer)).Generation);
    }

    [PostgreSqlFact]
    public async Task Unsaved_edits_of_a_previous_schema_are_projected_again_on_upgrade_so_the_current_editor_can_save_them()
    {
        // 3.5.2 → 3.6 upgrade test: the snapshot of the unsaved edits carried the old editor's projection into the new generation, the
        // current editor's save never equalled it (collaboration_conflict) and the edits never reached a revision.
        await using var f = await CollaborationFixture.CreateAsync();
        var authority = f.Host.Services.GetRequiredService<ILiveWriteAuthority>();
        var previous = CollaborationSchema.KnownHostSchemas[^2];
        var old = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        await f.Store.BindSchemaAsync(old.DocumentId, previous, default);
        using var saved = JsonDocument.Parse("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Uloženo"}]}]}""");
        using var typed = JsonDocument.Parse("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Uloženo"}]},{"type":"paragraph","content":[{"type":"text","text":"Neuloženo"}]}]}""");
        var seed = await authority.SeedAsync(previous, saved.RootElement, default);
        var update = await authority.WriteAsync(previous, seed.YjsState, [], typed.RootElement, 1, default);
        await f.Append(old.DocumentId, Guid.NewGuid(), update);
        var head = await authority.CheckpointAsync(previous, seed.YjsState, [update], 1, default);
        // The old editor's projection of that state: here simply one that is not this host's.
        await f.Store.FlushAsync(old.DocumentId, head with { Projections = head.Projections with { EditorJson = seed.Projections.EditorJson } }, default);

        var reopened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        Assert.NotEqual(old.Generation, reopened.Generation);
        await f.Host.Services.GetRequiredService<LiveRequirementsUpgrade>().ConvertAsync(reopened.DocumentId, default);

        using var session = f.Database.OpenSession();
        using var tx = session.BeginTransaction();
        var page = await session.GetAsync<Page>(f.Page.Id, LockMode.Upgrade);
        var checkpoint = await f.Store.ValidateCheckpointAsync(session, page, reopened.Generation, 1, default);
        Assert.Equal(head.Projections.EditorJson.GetRawText(), checkpoint.EditorJson);
    }

    [PostgreSqlFact]
    public async Task Additive_attachment_schema_upgrade_preserves_uncheckpointed_state_and_fences_old_generation()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var old = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        await f.Store.BindSchemaAsync(old.DocumentId, new(2,"rich",[new("youtrack",1)]),default);
        var covered = Guid.NewGuid(); var pending = Guid.NewGuid();
        await f.Append(old.DocumentId,covered,[1,2,3]);
        await f.Store.FlushAsync(old.DocumentId,CollaborationFixture.Snapshot(1),default);
        await f.Append(old.DocumentId,pending,[4,5,6]);
        var preservedBefore = await CapturePersistedAsync(f, true);
        var sessions = new CollaborationSessions();
        var actor = sessions.Register(f.Writer);
        var authenticator = new AtlasCollaborationAuthenticator(f.Host.Services.GetRequiredService<IServiceScopeFactory>(),sessions);
        var reopened = await Task.WhenAll(f.Store.OpenAsync(f.Page.Id,f.Writer),f.Store.OpenAsync(f.Page.Id,f.Writer));
        Assert.NotEqual(old.Generation,reopened[0].Generation);
        Assert.Equal(reopened[0].Generation,reopened[1].Generation);
        Assert.Equal(preservedBefore, await CapturePersistedAsync(f, true));
        Assert.True(await f.Store.HasExternalChangeAsync(old.DocumentId,default));
        Assert.False((await authenticator.AuthorizeAsync(actor,old.DocumentId,default)).CanWrite);
        Assert.True((await authenticator.AuthorizeAsync(actor,reopened[0].DocumentId,default)).CanWrite);
        await Assert.ThrowsAsync<DocumentSupersededException>(() => f.Append(old.DocumentId,Guid.NewGuid(),[9]));
        var migrated = (await f.Store.LoadAsync(reopened[0].DocumentId,default))!;
        Assert.Equal(new byte[]{7,8},migrated.Snapshot);
        Assert.Equal(2,migrated.Seq); Assert.Equal(new byte[]{4,5,6},Assert.Single(migrated.Updates).Update);
        Assert.Equal(new byte[]{1,2,3},migrated.RecentUpdates[covered].Update);
        Assert.Equal(new byte[]{4,5,6},migrated.RecentUpdates[pending].Update);
        Assert.True((await f.Append(reopened[0].DocumentId,covered,[1,2,3])).Duplicate);
        Assert.Equal(CurrentSchema.Plugins, migrated.Schema!.Plugins);
        Assert.Equal(1,await f.RevisionCount());
        await f.Store.FlushAsync(reopened[0].DocumentId,CollaborationFixture.Snapshot(2),default);
        using var session = f.Database.OpenSession(); using var tx = session.BeginTransaction();
        var page = await session.GetAsync<Page>(f.Page.Id,LockMode.Upgrade);
        var checkpoint = await f.Store.ValidateCheckpointAsync(session,page,reopened[0].Generation,2,default);
        Assert.Equal(2,checkpoint.Sequence);
    }
    [PostgreSqlFact]
    public async Task Additive_schema_upgrade_does_not_guess_unknown_plugin_versions_and_failure_rolls_back_all_data()
    {
        await using var f = await CollaborationFixture.CreateAsync(isolated: true) /* installs a failing trigger */;
        var old = await f.Store.OpenAsync(f.Page.Id,f.Writer);
        await f.Store.BindSchemaAsync(old.DocumentId,new(2,"rich",[new("youtrack",2)]),default);
        Assert.Equal(old.Generation,(await f.Store.OpenAsync(f.Page.Id,f.Writer)).Generation);
        using (var session = f.Database.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            var state = await session.GetAsync<PageCollaboration>(f.Page.Id,LockMode.Upgrade);
            state.SchemaJson=JsonSerializer.Serialize(new MarkdownDocumentSchema(2,"rich",[new("youtrack",1)]));
            await tx.CommitAsync();
        }
        var update = Guid.NewGuid(); await f.Append(old.DocumentId,update,[11,12]);
        await f.Store.FlushAsync(old.DocumentId,CollaborationFixture.Snapshot(1),default);
        using (var session = f.Database.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            await session.CreateSQLQuery("CREATE FUNCTION fail_attachment_upgrade() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Simulated migration write failure'; END $$").ExecuteUpdateAsync();
            await session.CreateSQLQuery("CREATE TRIGGER fail_attachment_upgrade BEFORE UPDATE ON \"CollaborationUpdate\" FOR EACH ROW EXECUTE FUNCTION fail_attachment_upgrade()").ExecuteUpdateAsync();
            await tx.CommitAsync();
        }
        var rollbackBefore = await CapturePersistedAsync(f, false);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Store.OpenAsync(f.Page.Id,f.Writer));
        Assert.Equal(rollbackBefore, await CapturePersistedAsync(f, false));
        var retained=(await f.Store.LoadAsync(old.DocumentId,default))!;
        Assert.Equal(new byte[]{7,8},retained.Snapshot);
        Assert.Equal(new byte[]{11,12},retained.RecentUpdates[update].Update);
        Assert.Equal("youtrack",Assert.Single(retained.Schema!.Plugins).Id);
        Assert.Equal(1,retained.Seq); Assert.Equal(1,await f.RevisionCount());
    }


    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Atomic_append_deduplicates_validates_payload_and_survives_store_recreation()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        var updateId = Guid.NewGuid();
        var results = await Task.WhenAll(
            f.Append(opened.DocumentId, updateId, [1, 2, 3]),
            f.Append(opened.DocumentId, updateId, [1, 2, 3]));
        Assert.All(results, result => Assert.Equal(1, result.Sequence));
        Assert.Single(results, result => result.Duplicate);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Append(opened.DocumentId, updateId, [9]));
        var stored = (await f.Store.LoadAsync(opened.DocumentId, default))!;
        Assert.Equal(1, stored.Seq);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(stored.Updates).Update);
        Assert.Equal(new byte[] { 1, 2, 3 }, stored.RecentUpdates[updateId].Update);
        Assert.Equal("", stored.SeedMarkdown);
        Assert.Equal("doc", stored.SeedEditorJson!.Value.GetProperty("type").GetString());
        Assert.Equal(TimeSpan.Zero, Assert.Single(stored.Updates).ReceivedAtUtc.Offset);
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(1), default);
        Assert.True((await f.Append(opened.DocumentId, updateId, [1, 2, 3])).Duplicate);
        Assert.Equal(1, await f.RevisionCount());
    }

    [PostgreSqlFact]
    public async Task Flush_and_bind_store_only_what_the_server_validated()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        // A negotiated schema with a plugin no Atlas release knows is refused before it is stored.
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.BindSchemaAsync(opened.DocumentId, new(2, "rich", [new("macro", 1)]), default));
        await f.Store.BindSchemaAsync(opened.DocumentId, CurrentSchema, default);
        await f.Append(opened.DocumentId, Guid.NewGuid(), [1]);
        // A projection the editor would refuse (an attribute a paragraph does not have) is refused: nothing of the snapshot is stored,
        // the relay is not made to retry it every tick, and a save of the live document says why instead of waiting forever (D-M3).
        var snapshot = CollaborationFixture.Snapshot(1);
        using (var invalid = JsonDocument.Parse("""{"type":"doc","content":[{"type":"paragraph","attrs":{"nope":1}}]}"""))
        {
            await f.Store.FlushAsync(opened.DocumentId, snapshot with { Projections = snapshot.Projections with { EditorJson = invalid.RootElement.Clone() } }, default);
        }
        using (var read = f.Host.OpenSession())
        {
            Assert.Null((await read.GetAsync<PageCollaboration>(f.Page.Id)).ProjectionJson);
        }
        var live = new SavePageRequest("Živě", f.Page.ContentJson, "", 1, opened.Generation);
        var refused = await Assert.ThrowsAsync<ApiException>(() => f.Pages.SaveAsync(f.Page.Id, live, f.Writer, default));
        Assert.Equal((400, "invalid_document", "document.content[0].attrs.nope"), (refused.Status, refused.Code, refused.Extensions?["path"]));
        Assert.StartsWith("Společné změny nejde uložit, server je odmítl: Obsah editoru neodpovídá", refused.Message, StringComparison.Ordinal);
        // The next snapshot the editor sends replaces the refusal.
        await f.Store.FlushAsync(opened.DocumentId, snapshot, default);
        Assert.Equal(2, (await f.Pages.SaveAsync(f.Page.Id, live, f.Writer, default)).Version);
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Snapshot_is_atomic_schema_bound_and_checkpoint_requires_current_durable_projection()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        var schema = new MarkdownDocumentSchema(1, "rich", []);
        Assert.Equal(schema.Profile, (await f.Store.BindSchemaAsync(opened.DocumentId, schema, default))!.Profile);
        await Assert.ThrowsAsync<DocumentSchemaException>(() => f.Store.BindSchemaAsync(opened.DocumentId, null, default));
        await f.Append(opened.DocumentId, Guid.NewGuid(), [1]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(2), default));
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(1), default);
        await f.Append(opened.DocumentId, Guid.NewGuid(), [2]);
        var loaded = (await f.Store.LoadAsync(opened.DocumentId, default))!;
        Assert.Equal(new byte[] { 7, 8 }, loaded.Snapshot);
        Assert.Equal(2, Assert.Single(loaded.Updates).Seq);
        Assert.Equal(2, loaded.Seq);
        Assert.Equal("rich", loaded.Schema!.Profile);
        using (var session = f.Database.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            var page = await session.GetAsync<Page>(f.Page.Id, LockMode.Upgrade);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Store.ValidateCheckpointAsync(session, page, opened.Generation, null, default))).Status);
        }
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(2), default);
        using (var session = f.Database.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            var page = await session.GetAsync<Page>(f.Page.Id, LockMode.Upgrade);
            var checkpoint = await f.Store.ValidateCheckpointAsync(session, page, opened.Generation, null, default);
            Assert.Equal(2, checkpoint.Sequence);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Store.ValidateCheckpointAsync(session, page, opened.Generation, 1, default))).Status);
        }
        // An older snapshot than the stored one is superseded (3.6-H: the server's checkpoint of a live AI write while the relay flushed an
        // older one) and leaves the stored one.
        Assert.True((await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(1), default)).IsSuperseded);
        using (var session = f.Database.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            Assert.Equal(2, (await f.Store.ValidateCheckpointAsync(session, await session.GetAsync<Page>(f.Page.Id, LockMode.Upgrade), opened.Generation, null, default)).Sequence);
        }
        Assert.Equal(1, await f.RevisionCount());
    }

    [PostgreSqlFact]
    public async Task Reset_fences_old_generation_and_rollback_preserves_previous_live_state()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        await f.Append(opened.DocumentId, Guid.NewGuid(), [3]);
        using (var session = f.Database.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            var page = await session.GetAsync<Page>(f.Page.Id, LockMode.Upgrade);
            var revision = await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync();
            await f.Store.ResetAsync(session, page, revision, default);
            await tx.RollbackAsync();
        }
        Assert.Equal(1, (await f.Store.LoadAsync(opened.DocumentId, default))!.Seq);
        using (var session = f.Database.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            var page = await session.GetAsync<Page>(f.Page.Id, LockMode.Upgrade);
            var revision = await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync();
            await f.Store.ResetAsync(session, page, revision, default);
            await tx.CommitAsync();
        }
        var reopened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        Assert.NotEqual(opened.Generation, reopened.Generation);
        Assert.Equal(0, (await f.Store.LoadAsync(reopened.DocumentId, default))!.Seq);
        await Assert.ThrowsAsync<DocumentSupersededException>(() => f.Append(opened.DocumentId, Guid.NewGuid(), [4]));
        // A snapshot of the replaced generation is dropped, not refused: the relay retried a refusal every tick for good.
        Assert.True((await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(1), default)).IsSuperseded);
        Assert.Null((await f.Store.LoadAsync(reopened.DocumentId, default))!.Snapshot);
        Assert.True(await f.Store.HasExternalChangeAsync(opened.DocumentId, default));
    }

    [PostgreSqlFact]
    public async Task Private_drafts_and_revoked_writer_rights_fail_closed_and_ids_are_strict()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.Store.OpenAsync(f.Page.Id, f.Reader))).Status);
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        await Assert.ThrowsAsync<FormatException>(() => f.Store.LoadAsync("page:" + f.Page.Id.ToString("D") + ":bad", default));
        using (var session = f.Database.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            await session.CreateSQLQuery("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=:id")
                .SetParameter("id", f.Writer.Id).ExecuteUpdateAsync();
            await tx.CommitAsync();
        }
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.Append(opened.DocumentId, Guid.NewGuid(), [1]))).Status);
        Assert.Equal(0, (await f.Store.LoadAsync(opened.DocumentId, default))!.Seq);
    }

    [PostgreSqlFact]
    public async Task Snapshot_compaction_bounds_receipts_without_losing_unflushed_updates()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        await f.SeedLog(opened.Generation, 4096, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Append(opened.DocumentId, Guid.NewGuid(), [2]));
        Assert.Equal(4096, (await f.Store.LoadAsync(opened.DocumentId, default))!.Updates.Count);
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(4096), default);
        await f.Append(opened.DocumentId, Guid.NewGuid(), [2]);
        var compacted = (await f.Store.LoadAsync(opened.DocumentId, default))!;
        // The append itself prunes nothing (COL2-6); the receipts are bounded again by the next stored snapshot.
        Assert.Equal(4097, compacted.RecentUpdates.Count);
        Assert.Equal(4097, Assert.Single(compacted.Updates).Seq);
        using (var session = f.Database.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            var page = await session.GetAsync<Page>(f.Page.Id, LockMode.Upgrade);
            var revision = await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync();
            await f.Store.ResetAsync(session, page, revision, default);
            await tx.CommitAsync();
        }
        var reset = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        await f.SeedLog(reset.Generation, 8, 3 * 1024 * 1024);
        await f.Store.FlushAsync(reset.DocumentId, CollaborationFixture.Snapshot(8), default);
        var bounded = (await f.Store.LoadAsync(reset.DocumentId, default))!;
        Assert.Empty(bounded.Updates);
        Assert.Equal(5, bounded.RecentUpdates.Count);
        Assert.True(bounded.RecentUpdates.Values.Sum(update => (long)update.Update.Length) <= 16 * 1024 * 1024);
    }

    [PostgreSqlFact]
    public async Task Authenticator_requires_same_handshake_proof_current_rights_and_generation()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var sessions = new CollaborationSessions();
        var authenticator = new AtlasCollaborationAuthenticator(f.Host.Services.GetRequiredService<IServiceScopeFactory>(), sessions);
        var writer = sessions.Register(f.Writer);
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        Assert.Equal(DocAccess.Write, await authenticator.AuthorizeAsync(writer, opened.DocumentId, default));
        Assert.Equal(DocAccess.Denied, await authenticator.AuthorizeAsync(new(writer.Id, writer.Name), opened.DocumentId, default));
        Assert.Equal(DocAccess.Denied, await authenticator.AuthorizeAsync(sessions.Register(f.Reader), opened.DocumentId, default));
        Assert.Equal(DocAccess.Denied, await authenticator.AuthorizeAsync(writer, new WikiDocumentId(f.Page.Id, Guid.NewGuid()).ToString(), default));
        using (var session = f.Database.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            var user = await session.GetAsync<UserAccount>(f.Writer.Id);
            user.SecurityStamp = "rotated";
            await AccountHistory.RecordAsync(session, user, null, CancellationToken.None);
            await tx.CommitAsync();
        }
        Assert.Equal(DocAccess.Denied, await authenticator.AuthorizeAsync(writer, opened.DocumentId, default));
    }

    [PostgreSqlFact]
    public async Task Business_save_and_restore_fence_live_generations_without_corrupting_history()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var pages = f.Pages;
        var history = f.History;
        var original = Assert.Single((await history.ListAsync(f.Page.Id, f.Writer)).Items);
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        await f.Append(opened.DocumentId, Guid.NewGuid(), [1]);
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(1), default);
        var saved = await pages.SaveAsync(f.Page.Id,
            new("Checkpoint", f.Page.ContentJson, "Snapshot", 1, opened.Generation), f.Writer, default);
        Assert.Equal(2, saved.Version);
        Assert.Equal(opened.Generation, (await f.Store.OpenAsync(f.Page.Id, f.Writer)).Generation);
        using (var session = f.Database.OpenSession())
        {
            var state = await session.GetAsync<PageCollaboration>(f.Page.Id);
            var current = await session.QueryOver<PageRevision>().Where(x => x.PageId == f.Page.Id && x.ActiveTo == null).SingleOrDefaultAsync();
            Assert.Equal(current.Id, state.BaseRevisionId);
        }
        var conflict = await Assert.ThrowsAsync<ApiException>(() => pages.SaveAsync(f.Page.Id,
            new("Stale", "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Stale\"}]}]}}", "Snapshot", 2, opened.Generation), f.Writer, default));
        Assert.Equal("collaboration_conflict", conflict.Code);
        await f.Append(opened.DocumentId, Guid.NewGuid(), [2]);
        var pending = await Assert.ThrowsAsync<ApiException>(() => pages.SaveAsync(f.Page.Id,
            new("Pending", f.Page.ContentJson, "Snapshot", 2, opened.Generation), f.Writer, default));
        Assert.Equal("collaboration_pending", pending.Code);
        Assert.Equal(2, await f.RevisionCount());
        var external = await pages.SaveAsync(f.Page.Id,
            new("External", f.Page.ContentJson, "External", 2), f.Writer, default);
        Assert.Equal(3, external.Version);
        var afterExternal = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        Assert.NotEqual(opened.Generation, afterExternal.Generation);
        await Assert.ThrowsAsync<DocumentSupersededException>(() => f.Append(opened.DocumentId, Guid.NewGuid(), [3]));
        var restored = await history.RestoreAsync(f.Page.Id, new(original.Id, 3), f.Writer);
        Assert.Equal(4, restored.Version);
        var afterRestore = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        Assert.NotEqual(afterExternal.Generation, afterRestore.Generation);
        await f.Append(afterRestore.DocumentId, Guid.NewGuid(), [4]);
        var same = (await history.ListAsync(f.Page.Id, f.Writer)).Items[0];
        Assert.Equal(4, (await history.RestoreAsync(f.Page.Id, new(same.Id, 4), f.Writer)).Version);
        Assert.NotEqual(afterRestore.Generation, (await f.Store.OpenAsync(f.Page.Id, f.Writer)).Generation);
        Assert.Equal(4, await f.RevisionCount());
    }

    [PostgreSqlFact]
    public async Task Live_save_without_content_stores_the_server_projection()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        await f.Append(opened.DocumentId, Guid.NewGuid(), [1]);
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(1), default);
        // The web's checkpoint of a live page sends no content: the server saves its own projection of the shared log.
        var saved = await f.Pages.SaveAsync(f.Page.Id, new("Checkpoint", null!, null, 1, opened.Generation), f.Writer, default);
        Assert.Equal(2, saved.Version);
        using (var session = f.Database.OpenSession())
        {
            var current = await session.QueryOver<PageRevision>().Where(x => x.PageId == f.Page.Id && x.ActiveTo == null).SingleOrDefaultAsync();
            Assert.Equal("Checkpoint", current.Title);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\"}]}"), JsonNode.Parse(current.ContentJson)!["document"]));
        }
        await f.Append(opened.DocumentId, Guid.NewGuid(), [2]);
        var pending = await Assert.ThrowsAsync<ApiException>(() => f.Pages.SaveAsync(f.Page.Id, new("Checkpoint", null!, null, 2, opened.Generation), f.Writer, default));
        Assert.Equal("collaboration_pending", pending.Code);
        // Without a live session there is nothing to take it from.
        var empty = await Assert.ThrowsAsync<ApiException>(() => f.Pages.SaveAsync(f.Page.Id, new("Checkpoint", null!, null, 2), f.Writer, default));
        Assert.Equal(400, empty.Status);
    }

    private static async Task<(string State, string Log)> CapturePersistedAsync(CollaborationFixture f, bool allowSchemaTransition)
    {
        using var session = f.Database.OpenSession();
        var stateExpression = allowSchemaTransition ? "to_jsonb(s) - 'Generation' - 'SchemaJson' - 'UpdatedAt'" : "to_jsonb(s)";
        var logExpression = allowSchemaTransition ? "to_jsonb(u) - 'Generation'" : "to_jsonb(u)";
        var state = await session.CreateSQLQuery($"SELECT CAST({stateExpression} AS text) FROM \"PageCollaboration\" s WHERE \"PageId\"=:page")
            .SetParameter("page", f.Page.Id).UniqueResultAsync<string>();
        var log = await session.CreateSQLQuery($"SELECT CAST(jsonb_agg({logExpression} ORDER BY \"Sequence\") AS text) FROM \"CollaborationUpdate\" u WHERE \"PageId\"=:page")
            .SetParameter("page", f.Page.Id).UniqueResultAsync<string>();
        return (state, log);
    }
}
