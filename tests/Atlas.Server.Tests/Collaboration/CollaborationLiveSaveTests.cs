using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>COL2-11: a writer of a live session saves after another writer of the same session saved (or published) meanwhile.</summary>
public sealed class CollaborationLiveSaveTests
{
    [PostgreSqlFact]
    public async Task A_live_save_over_a_version_saved_by_the_same_session_is_no_conflict_but_a_title_or_foreign_save_still_is()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        await f.Append(opened.DocumentId, System.Guid.NewGuid(), [1]);
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(1), default);
        // A saves the live document: version 2, the session's checkpoint.
        Assert.Equal(2, (await f.Pages.SaveAsync(f.Page.Id, new("Živě", f.Page.ContentJson, "", 1, opened.Generation), f.Writer, default)).Version);
        // B still knows version 1. Its live save holds what A saved, so it goes through.
        await f.Append(opened.DocumentId, System.Guid.NewGuid(), [2]);
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(2), default);
        Assert.Equal(2, (await f.Pages.SaveAsync(f.Page.Id, new("Živě", f.Page.ContentJson, "", 1, opened.Generation), f.Writer, default)).Version);
        // The title is not live: an older title would undo A's, so that stays a conflict.
        Assert.Equal("version_conflict", (await Assert.ThrowsAsync<ApiException>(() =>
            f.Pages.SaveAsync(f.Page.Id, new("Starý název", f.Page.ContentJson, "", 1, opened.Generation), f.Writer, default))).Code);
        // Without the session's generation the version guard is unchanged.
        Assert.Equal("version_conflict", (await Assert.ThrowsAsync<ApiException>(() =>
            f.Pages.SaveAsync(f.Page.Id, new("Živě", f.Page.ContentJson, "", 1), f.Writer, default))).Code);
    }

    /// <summary>
    /// storage-export e2e: a writer's editor rejoins after an API save (which declares no plugins) and checkpoints the same document with
    /// the editor's plugin declarations. That changes nothing: no new revision supersedes the saved one (the PDF autosync exports it).
    /// </summary>
    [PostgreSqlFact]
    public async Task A_live_save_of_the_stored_document_with_the_editors_plugin_declarations_is_no_new_revision()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        await f.Append(opened.DocumentId, System.Guid.NewGuid(), [1]);
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(1), default);
        var declared = f.Page.ContentJson.Replace("\"version\":1,", "\"version\":1,\"plugins\":[{\"id\":\"toc\",\"version\":1}],");
        Assert.NotEqual(f.Page.ContentJson, declared);

        Assert.Equal(1, (await f.Pages.SaveAsync(f.Page.Id, new("Initial", declared, "", 1, opened.Generation), f.Writer, default)).Version);
        Assert.Equal(1, await f.RevisionCount());
    }

    /// <summary>
    /// Campaign 2026-11-05: the snapshotter went quiet after an update. A save waits (collaboration_pending, nothing stored) and goes through,
    /// with nothing of the log lost, once the next snapshotter (MarkdownSyncHub.TakeSnapshotterFromPassivePeer) stores a snapshot covering it.
    /// </summary>
    [PostgreSqlFact]
    public async Task A_save_waits_while_the_last_update_has_no_snapshot_and_goes_through_once_the_next_snapshotter_stores_one()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        await f.Append(opened.DocumentId, System.Guid.NewGuid(), [1]);
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(1), default);
        await f.Append(opened.DocumentId, System.Guid.NewGuid(), [2]); // the snapshotter's own update; then it falls silent

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal("collaboration_pending", (await Assert.ThrowsAsync<ApiException>(() =>
                f.Pages.SaveAsync(f.Page.Id, new("Initial", f.Page.ContentJson, "", 1, opened.Generation), f.Writer, default))).Code);
        }
        Assert.Equal(1, await f.RevisionCount());

        await f.Append(opened.DocumentId, System.Guid.NewGuid(), [3]); // the next writer's update takes the role…
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(3), default); // …and its snapshot covers all three
        await f.Pages.SaveAsync(f.Page.Id, new("Initial", f.Page.ContentJson, "", 1, opened.Generation), f.Writer, default);
        using var session = f.Host.OpenSession();
        var state = await session.GetAsync<PageCollaboration>(f.Page.Id);
        Assert.Equal((3L, 3L), (state.Sequence, state.LastSavedSequence));
    }
}
