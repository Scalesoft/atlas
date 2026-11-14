using NHibernate;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Collaboration;

public sealed class CollaborationPersistenceTests
{
    [PostgreSqlFact]
    public async Task Log_uses_the_composite_key_projects_payload_size_and_follows_the_generation_cascade()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_collab_map_");
        var writer = await host.SeedUserAsync("writer");
        var space = await host.SeedSpaceAsync("Collaboration", (writer, SpaceRole.Writer));
        var pageId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var generation = Guid.NewGuid();
        await host.SeedAsync(async session =>
        {
            await session.SaveAsync(new Page { Id = pageId, SpaceId = space, OwnerId = writer.Id });
            await session.FlushAsync();
            await session.SaveAsync(new PageRevision
            {
                Channel = ContentChannels.Web,
                Id = revisionId,
                PageId = pageId,
                Version = 1,
                Title = "Live",
                ContentJson = "{}",
                Markdown = "live",
                AuthorId = writer.Id,
                ActiveFrom = DateTime.UtcNow
            });
            await session.FlushAsync();
            await session.SaveAsync(new PageCollaboration
            {
                PageId = pageId,
                Generation = generation,
                BaseRevisionId = revisionId,
                Sequence = 2,
                SnapshotSequence = 1,
                State = [7, 8],
                StateSha256 = new string('a', 64),
                ProjectionJson = "{}"
            });
            await session.FlushAsync();
            await session.SaveAsync(new CollaborationUpdate
            {
                PageId = pageId,
                Generation = generation,
                Sequence = 1,
                ClientUpdateId = Guid.NewGuid(),
                UserId = writer.Id,
                ReceivedAt = DateTime.UtcNow,
                Payload = [1, 2, 3]
            });
            await session.SaveAsync(new CollaborationUpdate
            {
                PageId = pageId,
                Generation = generation,
                Sequence = 2,
                ClientUpdateId = Guid.NewGuid(),
                UserId = writer.Id,
                ReceivedAt = DateTime.UtcNow,
                Payload = [4, 5, 6, 7, 8]
            });
        });
        using (var read = host.OpenSession())
        {
            var second = await read.GetAsync<CollaborationUpdate>(new CollaborationUpdate { PageId = pageId, Generation = generation, Sequence = 2 });
            Assert.Equal(new byte[] { 4, 5, 6, 7, 8 }, second.Payload);
            Assert.Equal(DateTimeKind.Utc, second.ReceivedAt.Kind);
            var sizes = await read.QueryOver<CollaborationUpdateSize>()
                .Where(x => x.PageId == pageId && x.Generation == generation)
                .OrderBy(x => x.Sequence).Desc
                .SelectList(list => list.Select(x => x.Sequence).Select(x => x.Bytes))
                .ListAsync<object[]>();
            Assert.Equal(new[] { 2L, 1L }, sizes.Select(row => (long)row[0]).ToArray());
            Assert.Equal(new[] { 5, 3 }, sizes.Select(row => (int)row[1]).ToArray());
        }
        var next = Guid.NewGuid();
        using (var write = host.OpenSession())
        using (var transaction = write.BeginTransaction())
        {
            var state = await write.GetAsync<PageCollaboration>(pageId, LockMode.Upgrade);
            state.Generation = next;
            await transaction.CommitAsync();
        }
        using (var read = host.OpenSession())
        {
            // ON UPDATE CASCADE (migration 017) moves the log with the generation; DynamicUpdate keeps the snapshot bytes.
            var state = await read.GetAsync<PageCollaboration>(pageId);
            Assert.Equal(new byte[] { 7, 8 }, state.State);
            Assert.Equal("{}", state.ProjectionJson);
            Assert.Equal(2, await read.QueryOver<CollaborationUpdateSize>().Where(x => x.Generation == next).RowCountAsync());
        }
    }
    [PostgreSqlFact]
    public async Task Size_projection_needs_an_explicit_flush_and_log_entities_need_eviction_after_a_generation_change()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_collab_flush_");
        var writer = await host.SeedUserAsync("writer");
        var space = await host.SeedSpaceAsync("Collaboration", (writer, SpaceRole.Writer));
        var pageId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var generation = Guid.NewGuid();
        await host.SeedAsync(async session =>
        {
            await session.SaveAsync(new Page { Id = pageId, SpaceId = space, OwnerId = writer.Id });
            await session.FlushAsync();
            await session.SaveAsync(new PageRevision
            {
                Channel = ContentChannels.Web,
                Id = revisionId,
                PageId = pageId,
                Version = 1,
                Title = "Live",
                ContentJson = "{}",
                Markdown = "live",
                AuthorId = writer.Id,
                ActiveFrom = DateTime.UtcNow
            });
            await session.FlushAsync();
            await session.SaveAsync(new PageCollaboration
            {
                PageId = pageId,
                Generation = generation,
                BaseRevisionId = revisionId,
                Sequence = 0
            });
        });
        using (var session = host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(new CollaborationUpdate
            {
                PageId = pageId,
                Generation = generation,
                Sequence = 1,
                ClientUpdateId = Guid.NewGuid(),
                UserId = writer.Id,
                ReceivedAt = DateTime.UtcNow,
                Payload = [1, 2, 3]
            });
            // The Subselect has its own query space and mapping by code has no Synchronize(...), so the pending
            // insert does NOT auto-flush. Reading the projection now silently misses the row.
            Assert.Equal(0, await session.QueryOver<CollaborationUpdateSize>().Where(x => x.PageId == pageId).RowCountAsync());
            await session.FlushAsync();
            var flushed = await session.QueryOver<CollaborationUpdateSize>().Where(x => x.PageId == pageId).SingleOrDefaultAsync();
            Assert.Equal(3, flushed.Bytes);
            await transaction.CommitAsync();
        }
        var next = Guid.NewGuid();
        using (var session = host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var stale = await session.GetAsync<CollaborationUpdate>(new CollaborationUpdate { PageId = pageId, Generation = generation, Sequence = 1 });
            var state = await session.GetAsync<PageCollaboration>(pageId, LockMode.Upgrade);
            state.Generation = next;
            await session.FlushAsync();
            // ON UPDATE CASCADE moved the log row, but the identity map still keys the loaded entity by the old
            // generation. Clearing the session is what makes the new key readable.
            Assert.Equal(generation, stale.Generation);
            session.Clear();
            var reloaded = await session.GetAsync<CollaborationUpdate>(new CollaborationUpdate { PageId = pageId, Generation = next, Sequence = 1 });
            Assert.NotNull(reloaded);
            Assert.NotSame(stale, reloaded);
            Assert.Equal(next, reloaded.Generation);
            Assert.Equal(new byte[] { 1, 2, 3 }, reloaded.Payload);
            await transaction.CommitAsync();
        }
    }
}
