using System.Security.Cryptography;
using System.Text.Json;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Queries;
using Xunit;

namespace Atlas.Server.Tests.PageOrganization;

public sealed class CrossSpaceMoveHandlerTests
{
    [PostgreSqlFact]
    public async Task Move_preserves_subtree_ids_history_and_publication_and_changes_current_access()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var target = await Target(f);
        var parent = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Root", PageOrganizationFixture.Rich("Root"), "Root"), f.Writer, default);
        var child = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, parent.Id, "Child", PageOrganizationFixture.Rich("Child"), "Child"), f.Writer, default);
        await f.Pages.PublishAsync(parent.Id, new PublishPageRequest(1), f.Writer, default);
        await f.Pages.PublishAsync(child.Id, new PublishPageRequest(1), f.Writer, default);
        var command = await Command(f, parent, target);
        var changed = await f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(parent.Id, command));
        Assert.Equal(2, changed.AffectedIds.Count);
        Assert.True(changed.SourceVersion > command.ExpectedSourceTreeVersion);
        Assert.True(changed.TargetVersion > command.ExpectedTargetTreeVersion);
        Assert.Empty((await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId))).Items);
        var result = await f.Pages.GetAsync(child.Id, f.Writer, default);
        Assert.Equal(target, result.SpaceId);
        Assert.Equal(parent.Id, result.ParentId);
        Assert.Equal(child.ContentJson, result.ContentJson);
        Assert.Equal(1, result.PublishedVersion);
        await Denied(404, () => f.Pages.GetAsync(parent.Id, f.Reader, default));
        await Denied(404, () => f.History.ListAsync(parent.Id, f.OtherWriter));
        using var session = f.Host.OpenSession();
        var history = await session.QueryOver<PageOrganizationRevision>().Where(x => x.PageId == parent.Id).OrderBy(x => x.ActiveFrom).Asc.ListAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal(f.SpaceId, history[0].SpaceId);
        Assert.Equal(target, history[1].SpaceId);
        Assert.Equal(f.Writer.Id, history[1].ActorId);
        Assert.Equal(history[0].ActiveTo, history[1].ActiveFrom);
        Assert.Null(history[1].ActiveTo);
        Assert.Equal(DateTimeKind.Utc, history[1].ActiveFrom.Kind);
        Assert.Equal(target, await session.CreateSQLQuery("SELECT \"SpaceId\" FROM \"SearchDocument\" WHERE \"PageId\"=:id").SetParameter("id", parent.Id).UniqueResultAsync<Guid>());
        // The Atlas 3 identity of every moved page follows it (its trigger), so a board card locks the right space.
        foreach (var id in new[] { parent.Id, child.Id })
        {
            var resource = await session.GetAsync<Atlas.Server.Platform.Resources.ContentResource>(id);
            Assert.Equal(("atlas.page", (Guid?)target), (resource.TypeId, resource.SpaceId));
        }
    }

    [PostgreSqlFact]
    public async Task Unsaved_updates_block_move_saved_nonzero_sequence_allows_it_and_old_generation_is_fenced()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var target = await Target(f);
        var page = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Live", PageOrganizationFixture.Rich("Live"), "Live"), f.Writer, default);
        var store = f.Collaboration;
        var live = await store.OpenAsync(page.Id, f.Writer);
        await store.AppendUpdateAsync(live.DocumentId, Guid.NewGuid(), f.Writer.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 1, 2 }, default);
        var command = await Command(f, page, target);
        var blocked = await Assert.ThrowsAsync<ApiException>(() => f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(page.Id, command)));
        Assert.Equal("collaboration_unsaved", blocked.Code);
        byte[] state = [7, 8];
        using var doc = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\"}]}");
        await store.FlushAsync(live.DocumentId, new(state, 1, new(1, Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant(), "Snapshot", doc.RootElement.Clone(), "Snapshot")), default);
        var rich = "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\"}]}}";
        var saved = await f.Pages.SaveAsync(page.Id, new SavePageRequest(page.Title, rich, "Snapshot", 1, live.Generation), f.Writer, default);
        await store.AppendUpdateAsync(live.DocumentId, Guid.NewGuid(), f.Writer.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 3, 4 }, default);
        await Denied(409, () => f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(page.Id, command with { ExpectedVersion = saved.Version })));
        await store.FlushAsync(live.DocumentId, new(state, 2, new(2, Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant(), "Snapshot", doc.RootElement.Clone(), "Snapshot")), default);
        // Same business content still acknowledges the latest durable sequence.
        saved = await f.Pages.SaveAsync(page.Id, new SavePageRequest(page.Title, rich, "Snapshot", saved.Version, live.Generation), f.Writer, default);
        await f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(page.Id, await Command(f, saved, target)));
        Assert.True(await store.HasExternalChangeAsync(live.DocumentId, default));
        await Assert.ThrowsAsync<Atlas.Markdown.Sync.DocumentSupersededException>(() => store.AppendUpdateAsync(live.DocumentId, Guid.NewGuid(), f.Writer.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 9 }, default));
        var reopened = await store.OpenAsync(page.Id, f.Writer);
        Assert.NotEqual(live.Generation, reopened.Generation);
        Assert.Equal(rich, (await f.Pages.GetAsync(page.Id, f.Writer, default)).ContentJson);
    }

    [PostgreSqlFact]
    public async Task Private_subtree_and_destination_rights_fail_closed_before_confirmation_and_noop_rename_does_not_acknowledge_edits()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var target = await Target(f);
        var page = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Root", PageOrganizationFixture.Rich("Root"), "Root"), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(1), f.Writer, default);
        await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, page.Id, "Hidden", PageOrganizationFixture.Rich("x"), "x"), f.OtherWriter, default);
        var command = await Command(f, page, target);
        await Denied(404, () => f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(page.Id, command with { IncludeSubtree = false })));
        await Denied(404, () => f.As<CrossSpaceChange>(f.OtherWriter, new MovePageToSpace(page.Id, command)));
        await Denied(400, () => f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(page.Id, command with { AcknowledgeAccessChange = false })));
        var own = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Own", PageOrganizationFixture.Rich("Own"), "Own"), f.Writer, default);
        var store = f.Collaboration;
        var live = await store.OpenAsync(own.Id, f.Writer);
        await store.AppendUpdateAsync(live.DocumentId, Guid.NewGuid(), f.Writer.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 1 }, default);
        await f.As(f.Writer, new RenamePage(own.Id, new RenamePageRequest("Renamed", 1)));
        var renamed = await f.As<PageDto>(f.Writer, new GetPage(own.Id));
        var renamedCommand = await Command(f, renamed, target);
        await Denied(409, () => f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(own.Id, renamedCommand)));
    }

    [PostgreSqlFact]
    public async Task Both_tree_versions_page_version_and_fresh_actor_are_required()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var target = await Target(f);
        var page = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Page", PageOrganizationFixture.Rich("Page"), "Page"), f.Writer, default);
        var command = await Command(f, page, target);
        await Denied(409, () => f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(page.Id, command with { ExpectedTargetTreeVersion = 999 })));
        await Denied(409, () => f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(page.Id, command with { ExpectedVersion = 999 })));

        async Task<bool> Move()
        {
            try
            {
                await f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(page.Id, command));
                return true;
            }
            catch (ApiException e) when (e.Status is 400 or 409)
            {
                return false;
            }
        }

        Assert.Single(await Task.WhenAll(Move(), Move()), x => x);
        await f.Execute("UPDATE \"UserAccount\" SET \"SecurityStamp\"='revoked' WHERE \"Id\"=:id", f.Writer.Id);
        var back = command with { TargetSpaceId = f.SpaceId };
        await Denied(401, () => f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(page.Id, back)));
    }

    [PostgreSqlFact]
    public async Task Inaccessible_root_never_exposes_tree_versions_and_trashed_children_or_revoked_target_grants_block_move()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var target = await Target(f);
        var hidden = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Private", PageOrganizationFixture.Rich("x"), "x"), f.OtherWriter, default);
        var hiddenCommand = new MovePageSpaceRequest(target, null, null, false, 999, 999, 999, true);
        await Denied(404, () => f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(hidden.Id, hiddenCommand)));
        await Denied(404, () => f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(hidden.Id, hiddenCommand with { TargetSpaceId = f.SpaceId })));
        var root = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Root", PageOrganizationFixture.Rich("Root"), "Root"), f.Writer, default);
        var child = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, root.Id, "Child", PageOrganizationFixture.Rich("Child"), "Child"), f.Writer, default);
        var sourceTree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        await f.As<OrganizationChange>(f.Writer, new TrashPage(child.Id, new TrashPageRequest(false, sourceTree.Version, child.Version)));
        var command = await Command(f, root, target);
        await Denied(409, () => f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(root.Id, command)));
        using (var session = f.Host.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            await session.CreateSQLQuery("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"SpaceId\"=:space AND \"UserId\"=:user AND \"ActiveTo\" IS NULL")
                .SetParameter("space", target).SetParameter("user", f.Writer.Id).ExecuteUpdateAsync();
            await tx.CommitAsync();
        }
        await Denied(404, () => f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(root.Id, command)));
        // A target it only reads is the missing right (403, review S3), told only after the page's own decision: for a page it
        // cannot see the answer stays the 404 of a missing page, so the 403 tells nobody that the id exists.
        await f.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = target, UserId = f.Writer.Id, Role = (int)SpaceRole.Reader }));
        await Denied(403, () => f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(root.Id, command)));
        await Denied(404, () => f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(hidden.Id, hiddenCommand)));
        Assert.Equal(f.SpaceId, (await f.Pages.GetAsync(root.Id, f.Writer, default)).SpaceId);
    }

    [PostgreSqlFact]
    public async Task Live_states_of_a_moved_subtree_are_checked_and_reset_in_statements_that_do_not_grow_with_it()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var target = await Target(f);
        async Task<int> LiveStatements(int size)
        {
            var root = await f.CreateAsync($"Root {size}");
            await f.Collaboration.OpenAsync(root, f.Writer);
            for (var i = 1; i < size; i++)
            {
                await f.Collaboration.OpenAsync(await f.CreateAsync($"Child {size}.{i}", root), f.Writer);
            }
            var command = await Command(f, await f.Pages.GetAsync(root, f.Writer, default), target);
            using var recording = Atlas.Server.Tests.Platform.SqlRecording.Start();
            Assert.Equal(size, (await f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(root, command))).AffectedIds.Count);
            return recording.Statements.Count(sql => sql.Contains("\"PageCollaboration\"") || sql.Contains("\"PageRevision\""));
        }
        Assert.Equal(await LiveStatements(3), await LiveStatements(30));
    }

    /// <summary>5.2: pages moved into a space that verifies its pages start as created ones, the mover as steward; a page's own row stays.</summary>
    [PostgreSqlFact]
    public async Task Pages_moved_into_a_verifying_space_get_the_mover_as_steward_and_keep_their_own_rows()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var target = await Target(f);
        await f.Host.ExecuteAsync("""UPDATE "Space" SET "VerificationIntervalMonths"=6 WHERE "Id"=:id""", target);
        var parent = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Root", PageOrganizationFixture.Rich("Root"), "Root"), f.Writer, default);
        var child = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, parent.Id, "Child", PageOrganizationFixture.Rich("Child"), "Child"), f.Writer, default);
        await f.Host.ExecuteAsync("""INSERT INTO "PageVerification"("PageId","IntervalMonths","NoticeStage") VALUES (:id,3,0)""", child.Id);
        await f.As<CrossSpaceChange>(f.Writer, new MovePageToSpace(parent.Id, await Command(f, parent, target)));
        using var session = f.Host.OpenSession();
        var rows = (await session.QueryOver<Atlas.Server.Features.PageVerifications.Domain.PageVerification>()
            .WhereRestrictionOn(x => x.PageId).IsIn(new[] { parent.Id, child.Id }).ListAsync()).ToDictionary(x => x.PageId);
        Assert.Equal(f.Writer.Id, rows[parent.Id].StewardId);
        Assert.Equal(((Guid?)null, (short?)3), (rows[child.Id].StewardId, rows[child.Id].IntervalMonths));
    }

    private static async Task<Guid> Target(PageOrganizationFixture f)
    {
        var id = Guid.NewGuid();
        await f.Host.SeedAsync(async session =>
        {
            await session.SaveAsync(new Space { Id = id, Name = "Target" });
            await session.FlushAsync();
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = id, UserId = f.Writer.Id, Role = 2 });
        });
        return id;
    }

    private static async Task<MovePageSpaceRequest> Command(PageOrganizationFixture f, PageDto page, Guid target)
    {
        return new MovePageSpaceRequest(target, null, null, true,
            (await f.As<PageTreeDto>(f.Writer, new GetPageTree(page.SpaceId))).Version,
            (await f.As<PageTreeDto>(f.Writer, new GetPageTree(target))).Version,
            page.Version, true);
    }

    private static async Task Denied(int status, Func<Task> action)
    {
        Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);
    }
}
