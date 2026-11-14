using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class SessionExtensionsTests
{
    [PostgreSqlFact]
    public async Task A_locking_read_of_a_held_instance_gives_the_row_under_the_lock_in_one_statement()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_session_lock_");
        var owner = await host.SeedUserAsync("owner");
        var page = new Page { Id = Guid.NewGuid(), SpaceId = await host.SeedSpaceAsync("Space"), OwnerId = owner.Id };
        await host.SeedAsync(session => session.SaveAsync(page));
        using var session = host.OpenSession();
        using var transaction = session.BeginTransaction();
        var held = await session.GetAsync<Page>(page.Id);
        var loaded = held.Version;
        // Another transaction changes the row behind the instance this session holds.
        await host.ExecuteAsync("UPDATE \"Page\" SET \"Version\"=\"Version\"+1 WHERE \"Id\"=:id", page.Id);

        using (var recording = SqlRecording.Start())
        {
            Assert.Same(held, await session.GetForUpdateAsync<Page>(page.Id, default));
            Assert.Equal(loaded + 1, held.Version);
            Assert.EndsWith("for update", Assert.Single(recording.Statements), StringComparison.OrdinalIgnoreCase);
        }
        using (var recording = SqlRecording.Start())
        {
            // The instance is under this transaction's lock now: nothing more is sent, as with a locking load.
            Assert.Same(held, await session.GetForUpdateAsync<Page>(page.Id, default));
            Assert.Empty(recording.Statements);
        }
    }
}
