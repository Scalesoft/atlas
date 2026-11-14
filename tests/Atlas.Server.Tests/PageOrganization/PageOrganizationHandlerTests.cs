using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Queries;
using Xunit;

namespace Atlas.Server.Tests.PageOrganization;

public sealed class PageOrganizationHandlerTests
{
    [PostgreSqlFact]
    public async Task Tree_orders_siblings_hides_foreign_concepts_and_reports_the_space_version()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var a = await f.CreateAsync("A");
        var b = await f.CreateAsync("B");
        await f.PublishAsync(a);
        await f.CreateAsync("Foreign private", a, f.OtherWriter);
        var writerTree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        Assert.Equal([a, b], writerTree.Items.Select(x => x.Id));
        Assert.All(writerTree.Items, x => Assert.True(x.CanEdit));
        Assert.True(writerTree.Version > 0);
        var readerTree = await f.As<PageTreeDto>(f.Reader, new GetPageTree(f.SpaceId));
        Assert.Equal(a, Assert.Single(readerTree.Items).Id);
        Assert.False(Assert.Single(readerTree.Items).CanEdit);
        await Denied(404, () => f.As<PageTreeDto>(f.Admin, new GetPageTree(Guid.NewGuid())));
        await Denied(403, () => f.As<PageTrashDto>(f.Writer, new ListPageTrash(f.SpaceId, 0, 50)));
        Assert.Empty((await f.As<PageTrashDto>(f.Admin, new ListPageTrash(f.SpaceId, 0, 50))).Items);
        await Denied(400, () => f.As<PageTrashDto>(f.Admin, new ListPageTrash(f.SpaceId, -1, 50)));
        await Denied(400, () => f.As<PageTrashDto>(f.Admin, new ListPageTrash(f.SpaceId, 0, int.MaxValue)));
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Move_reorders_reparents_without_content_changes_and_rejects_cycles()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var a = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "A", PageOrganizationFixture.Rich("A"), "A"), f.Writer, default);
        var b = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "B", PageOrganizationFixture.Rich("B"), "B"), f.Writer, default);
        var c = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "C", PageOrganizationFixture.Rich("C"), "C"), f.Writer, default);
        var tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, tree.Items.Select(x => x.Id));
        var moved = await f.As<OrganizationChange>(f.Writer, new MovePage(c.Id, new MovePageRequest(null, a.Id, tree.Version)));
        Assert.Equal(new[] { c.Id, a.Id, b.Id }, (await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId))).Items.Select(x => x.Id));
        await f.As<OrganizationChange>(f.Writer, new MovePage(b.Id, new MovePageRequest(a.Id, null, moved.Version)));
        tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        Assert.Equal(a.Id, tree.Items.Single(x => x.Id == b.Id).ParentId);
        Assert.All(tree.Items, x => Assert.Equal(1, x.Version));
        await Denied(400, () => f.As<OrganizationChange>(f.Writer, new MovePage(a.Id, new MovePageRequest(b.Id, null, tree.Version))));
        using var session = f.Host.OpenSession();
        var intervals = await session.QueryOver<PageOrganizationRevision>().Where(x => x.PageId == c.Id).OrderBy(x => x.ActiveFrom).Asc.ListAsync();
        Assert.Equal(2, intervals.Count);
        Assert.Equal(intervals[1].ActiveFrom, intervals[0].ActiveTo);
        Assert.Null(intervals[1].ActiveTo);
        Assert.All(intervals, x => Assert.Equal(DateTimeKind.Utc, x.ActiveFrom.Kind));
    }

    [PostgreSqlFact]
    public async Task Tree_CAS_has_one_concurrent_winner_and_revalidates_actor()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var a = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "A", PageOrganizationFixture.Rich("A"), "A"), f.Writer, default);
        var b = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "B", PageOrganizationFixture.Rich("B"), "B"), f.Writer, default);
        var c = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "C", PageOrganizationFixture.Rich("C"), "C"), f.Writer, default);
        var tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));

        async Task<bool> Move(Guid id)
        {
            try
            {
                await f.As<OrganizationChange>(f.Writer, new MovePage(id, new MovePageRequest(a.Id, null, tree.Version)));
                return true;
            }
            catch (ApiException e) when (e.Status == 409)
            {
                return false;
            }
        }

        Assert.Single(await Task.WhenAll(Move(b.Id), Move(c.Id)), x => x);
        await f.Execute("UPDATE \"UserAccount\" SET \"SecurityStamp\"='revoked' WHERE \"Id\"=:id", f.Writer.Id);
        await Denied(401, () => f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId)));
        await Denied(401, () => f.As<OrganizationChange>(f.Writer, new MovePage(a.Id, new MovePageRequest(null, null, tree.Version))));
    }

    [PostgreSqlFact]
    public async Task Trash_requires_explicit_subtree_and_restore_preserves_ids_and_publication()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var a = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "A", PageOrganizationFixture.Rich("A"), "A"), f.Writer, default);
        var b = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, a.Id, "B", PageOrganizationFixture.Rich("B"), "B"), f.Writer, default);
        await f.Pages.PublishAsync(a.Id, new PublishPageRequest(1), f.Writer, default);
        var tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        await Denied(409, () => f.As<OrganizationChange>(f.Writer, new TrashPage(a.Id, new TrashPageRequest(false, tree.Version, 1))));
        var changed = await f.As<OrganizationChange>(f.Writer, new TrashPage(a.Id, new TrashPageRequest(true, tree.Version, 1)));
        Assert.Equal(2, changed.AffectedIds.Count);
        Assert.Empty((await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId))).Items);
        await Denied(403, () => f.As<PageTrashDto>(f.Writer, new ListPageTrash(f.SpaceId, 0, 50)));
        var trash = await f.As<PageTrashDto>(f.Admin, new ListPageTrash(f.SpaceId, 0, 50));
        Assert.Equal(2, trash.Items.Count);
        Assert.Single(trash.Items.Select(x => x.TrashBatchId).Distinct());
        // The Writer can read the page but lacks ManageAccess, so the refusal is disclosed (403).
        await Denied(403, () => f.As<OrganizationChange>(f.Writer, new UntrashPage(a.Id, new UntrashPageRequest(null, true, changed.Version))));
        await f.As<OrganizationChange>(f.Admin, new UntrashPage(a.Id, new UntrashPageRequest(null, true, changed.Version)));
        tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        Assert.Equal(2, tree.Items.Count);
        Assert.Equal(a.Id, tree.Items.Single(x => x.Id == b.Id).ParentId);
        Assert.Equal(1, (await f.Pages.GetAsync(a.Id, f.Reader, default)).PublishedVersion);
    }

    [PostgreSqlFact]
    public async Task Private_descendant_blocks_subtree_trash_and_reader_never_sees_concepts()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var a = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Public", PageOrganizationFixture.Rich("Public"), "Public"), f.Writer, default);
        await f.Pages.PublishAsync(a.Id, new PublishPageRequest(1), f.Writer, default);
        await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, a.Id, "Foreign private", PageOrganizationFixture.Rich("x"), "x"), f.OtherWriter, default);
        Assert.Single((await f.As<PageTreeDto>(f.Reader, new GetPageTree(f.SpaceId))).Items);
        var tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        Assert.Single(tree.Items);
        await Denied(404, () => f.As<OrganizationChange>(f.Writer, new TrashPage(a.Id, new TrashPageRequest(true, tree.Version, 1))));
        // The Reader can read the published page but lacks EditContent: 403.
        await Denied(403, () => f.As<OrganizationChange>(f.Reader, new MovePage(a.Id, new MovePageRequest(null, null, tree.Version))));
        Assert.Single((await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId))).Items);
    }

    [PostgreSqlFact]
    public async Task Restore_reports_duplicate_titles_and_validates_pagination()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var a = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Same", PageOrganizationFixture.Rich("Same"), "Same"), f.Writer, default);
        await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Same", PageOrganizationFixture.Rich("Same"), "Same"), f.Writer, default);
        var tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        var trash = await f.As<OrganizationChange>(f.Writer, new TrashPage(a.Id, new TrashPageRequest(false, tree.Version, 1)));
        var restored = await f.As<OrganizationChange>(f.Admin, new UntrashPage(a.Id, new UntrashPageRequest(null, false, trash.Version)));
        Assert.True(restored.HasSiblingTitleCollision);
        Assert.Equal(2, (await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId))).Items.Count);
        await Denied(400, () => f.As<PageTrashDto>(f.Admin, new ListPageTrash(f.SpaceId, -1, 50)));
        await Denied(400, () => f.As<PageTrashDto>(f.Admin, new ListPageTrash(f.SpaceId, 0, int.MaxValue)));
        await Denied(404, () => f.As<PageTreeDto>(f.Admin, new GetPageTree(Guid.NewGuid())));
    }

    [PostgreSqlFact]
    public async Task Rename_appends_title_revision_preserves_published_content_and_live_generation()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var a = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Original", PageOrganizationFixture.Rich("Original"), "Original"), f.Writer, default);
        await f.Pages.PublishAsync(a.Id, new PublishPageRequest(1), f.Writer, default);
        var store = f.Collaboration;
        var live = await store.OpenAsync(a.Id, f.Writer);
        // A revision stored before the server owned the projection carries a client's Markdown; the renamed one gets the server's (D-L3).
        await f.Execute("UPDATE \"PageRevision\" SET \"Markdown\"='client' WHERE \"PageId\"=:id", a.Id);
        await f.As(f.Writer, new RenamePage(a.Id, new RenamePageRequest("Renamed", 1)));
        var renamed = await f.As<PageDto>(f.Writer, new GetPage(a.Id));
        Assert.Equal(2, renamed.Version);
        Assert.Equal(a.ContentJson, renamed.ContentJson);
        Assert.Equal(a.Markdown, renamed.Markdown);
        Assert.Equal("Original", (await f.Pages.GetAsync(a.Id, f.Reader, default)).Title);
        Assert.Equal(live.Generation, (await store.OpenAsync(a.Id, f.Writer)).Generation);
        await Denied(409, () => f.As(f.Writer, new RenamePage(a.Id, new RenamePageRequest("Stale", 1))));
        await f.As(f.Writer, new RenamePage(a.Id, new RenamePageRequest("Renamed", 2)));
        Assert.Equal(2, (await f.As<PageDto>(f.Writer, new GetPage(a.Id))).Version);
    }

    [PostgreSqlFact]
    public async Task Rename_of_odd_legacy_content_keeps_its_stored_Markdown_and_refuses_a_projection_over_the_limit()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var id = await f.CreateAsync("Legacy");
        // A heading level the writer cannot repeat: the stored projection stays, the rename goes through.
        await f.Execute("""
            UPDATE "PageRevision" SET "Markdown"='stored', "ContentJson"='{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"heading","attrs":{"level":-1},"content":[{"type":"text","text":"A"}]}]}}'
            WHERE "PageId"=:id
            """, id);
        await f.As(f.Writer, new RenamePage(id, new RenamePageRequest("Renamed", 1)));
        var page = await f.As<PageDto>(f.Writer, new GetPage(id));
        Assert.Equal(("Renamed", "stored"), (page.Title, page.Markdown));

        // Text whose Markdown is over the limit: refused like any other write.
        await f.Execute("""
            UPDATE "PageRevision" SET "ContentJson"='{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"' || repeat('a', 1000001) || '"}]}]}}'
            WHERE "PageId"=:id AND "ActiveTo" IS NULL
            """, id);
        var error = await Assert.ThrowsAsync<ApiException>(() => f.As(f.Writer, new RenamePage(id, new RenamePageRequest("Again", 2))));
        Assert.Equal((400, "document_too_large"), (error.Status, error.Code));
    }

    [PostgreSqlFact]
    public async Task Rename_rechecks_the_locked_version_even_when_the_session_already_loaded_the_page()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var id = await f.CreateAsync("Loaded");
        using var scope = f.Host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        using var transaction = session.BeginTransaction();
        Assert.Equal(1, (await session.GetAsync<Page>(id)).Version);

        await f.As(f.Writer, new RenamePage(id, new RenamePageRequest("Theirs", 1)));
        var handler = ActivatorUtilities.CreateInstance<RenamePageHandler>(scope.ServiceProvider);
        var error = await Assert.ThrowsAsync<ApiException>(() => handler.Handle(new RenamePage(id, new RenamePageRequest("Mine", 1)), default));
        Assert.Equal((409, "version_conflict"), (error.Status, error.Code));
    }

    [PostgreSqlFact]
    public async Task Trash_fences_live_editor_and_hides_content_history_until_restore()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var a = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Live", PageOrganizationFixture.Rich("Live"), "Live"), f.Writer, default);
        await f.Pages.PublishAsync(a.Id, new PublishPageRequest(1), f.Writer, default);
        var store = f.Collaboration;
        var live = await store.OpenAsync(a.Id, f.Writer);
        var tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        var trashed = await f.As<OrganizationChange>(f.Writer, new TrashPage(a.Id, new TrashPageRequest(false, tree.Version, 1)));
        Assert.True(await store.HasExternalChangeAsync(live.DocumentId, default));
        await Denied(404, () => store.OpenAsync(a.Id, f.Writer));
        await Denied(404, () => f.Pages.GetAsync(a.Id, f.Reader, default));
        await Denied(404, () => f.Pages.SaveAsync(a.Id, new SavePageRequest("Lost", a.ContentJson, a.Markdown, 1), f.Writer, default));
        await Denied(404, () => f.History.ListAsync(a.Id, f.Writer));
        await f.As<OrganizationChange>(f.Admin, new UntrashPage(a.Id, new UntrashPageRequest(null, false, trashed.Version)));
        Assert.NotEqual(live.Generation, (await store.OpenAsync(a.Id, f.Writer)).Generation);
        Assert.Equal(a.ContentJson, (await f.Pages.GetAsync(a.Id, f.Reader, default)).ContentJson);
    }

    [PostgreSqlFact]
    public async Task Trash_refuses_over_unsaved_live_edits_of_others_anywhere_in_the_subtree_and_discards_only_the_actors_own()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var a = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "A", PageOrganizationFixture.Rich("A"), "A"), f.Writer, default);
        var b = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, a.Id, "B", PageOrganizationFixture.Rich("B"), "B"), f.Writer, default);
        await f.Pages.PublishAsync(a.Id, new PublishPageRequest(1), f.Writer, default);
        await f.Pages.PublishAsync(b.Id, new PublishPageRequest(1), f.Writer, default);
        var store = f.Collaboration;
        var own = await store.OpenAsync(a.Id, f.Writer);
        await store.AppendUpdateAsync(own.DocumentId, Guid.NewGuid(), f.Writer.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 1 });
        var foreign = await store.OpenAsync(b.Id, f.OtherWriter);
        await store.AppendUpdateAsync(foreign.DocumentId, Guid.NewGuid(), f.OtherWriter.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 2 });
        var tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));

        var refused = await Assert.ThrowsAsync<ApiException>(() => f.As<OrganizationChange>(f.Writer, new TrashPage(a.Id, new TrashPageRequest(true, tree.Version, 1))));
        Assert.Equal(409, refused.Status);
        Assert.Equal("collaboration_unsaved", refused.Code);
        Assert.Contains("podstránek", refused.Message);
        Assert.Equal(2, (await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId))).Items.Count);
        Assert.False(await store.HasExternalChangeAsync(own.DocumentId));
        Assert.Single((await store.LoadAsync(foreign.DocumentId))!.Updates);

        // Once the other editor saved, the actor's own unsaved edits go to the trash with the page.
        await f.Execute("UPDATE \"PageCollaboration\" SET \"LastSavedSequence\"=\"Sequence\" WHERE \"PageId\"=:id", b.Id);
        var trashed = await f.As<OrganizationChange>(f.Writer, new TrashPage(a.Id, new TrashPageRequest(true, tree.Version, 1)));
        Assert.Equal(2, trashed.AffectedIds.Count);
        Assert.True(await store.HasExternalChangeAsync(own.DocumentId));
    }

    [PostgreSqlFact]
    public async Task Gap_exhaustion_rebalances_siblings_and_foreign_or_private_destinations_are_rejected()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var a = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "A", PageOrganizationFixture.Rich("A"), "A"), f.Writer, default);
        var b = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "B", PageOrganizationFixture.Rich("B"), "B"), f.Writer, default);
        var c = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "C", PageOrganizationFixture.Rich("C"), "C"), f.Writer, default);
        await f.Execute("UPDATE \"Page\" SET \"SortOrder\"=1 WHERE \"Id\"=:id", a.Id);
        await f.Execute("UPDATE \"Page\" SET \"SortOrder\"=2 WHERE \"Id\"=:id", b.Id);
        var tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        await f.As<OrganizationChange>(f.Writer, new MovePage(c.Id, new MovePageRequest(null, b.Id, tree.Version)));
        tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        Assert.Equal(new[] { a.Id, c.Id, b.Id }, tree.Items.Select(x => x.Id));
        Assert.Equal(3, tree.Items.Select(x => x.SortOrder).Distinct().Count());
        var foreignSpace = Guid.NewGuid();
        await f.Host.SeedAsync(async session => await session.SaveAsync(new Space { Id = foreignSpace, Name = "Foreign" }));
        var foreign = await f.Pages.CreateAsync(new CreatePageRequest(foreignSpace, null, "Foreign", PageOrganizationFixture.Rich("f"), "f"), f.Admin, default);
        await Denied(400, () => f.As<OrganizationChange>(f.Writer, new MovePage(a.Id, new MovePageRequest(foreign.Id, null, tree.Version))));
        var hidden = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Hidden", PageOrganizationFixture.Rich("h"), "h"), f.OtherWriter, default);
        tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        await Denied(400, () => f.As<OrganizationChange>(f.Writer, new MovePage(a.Id, new MovePageRequest(hidden.Id, null, tree.Version))));
        await f.Execute("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL", f.Writer.Id);
        await Denied(404, () => f.As<OrganizationChange>(f.Writer, new MovePage(a.Id, new MovePageRequest(null, null, tree.Version))));
    }

    private static async Task Denied(int status, Func<Task> action)
    {
        Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);
    }
}
