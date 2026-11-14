using Atlas.Server.Features.Pages.Domain;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.PageOrganization;

/// <summary>
/// A page's insert takes its space FOR UPDATE before it checks the parent and takes the end of its level (ContentRowEffects, the
/// BEFORE INSERT trigger until 2026110930), so creates in one space run one after the other even on a path that locked nothing
/// before (an import): the second reads after the first committed. Two NHibernate sessions stand for two concurrent writers.
/// </summary>
public sealed class PagePlacementConcurrencyTests
{
    [PostgreSqlFact]
    public async Task Two_creates_in_one_space_never_get_the_same_place_or_a_parent_trashed_meanwhile()
    {
        await using var f = await PageOrganizationFixture.CreateAsync();
        var parent = await f.CreateAsync("Parent");

        // The first create holds its space until it commits; the second waits for it and takes the place after it.
        using var first = f.Host.OpenSession();
        using var firstTransaction = first.BeginTransaction();
        var placed = Child(f, parent);
        await first.SaveAsync(placed);
        await first.FlushAsync();
        using var second = f.Host.OpenSession();
        using var secondTransaction = second.BeginTransaction();
        // The backend's pid before anything is pending: a native query would flush the insert first.
        var secondPid = await BackendPidAsync(second);
        var after = Child(f, parent);
        await second.SaveAsync(after);
        var waiting = second.FlushAsync();
        await WaitForLockWaitAsync(f.Host.ConnectionString, secondPid, waiting);
        Assert.False(waiting.IsCompleted);
        await firstTransaction.CommitAsync();
        await waiting;
        await secondTransaction.CommitAsync();
        Assert.Equal(1024, placed.SortOrder);
        Assert.Equal(2048, after.SortOrder);

        // A trash of the parent (under the space lock, as every organization change) commits while a create under it waits: refused.
        using var trash = f.Host.OpenSession();
        using var trashTransaction = trash.BeginTransaction();
        await trash.CreateSQLQuery("""SELECT "Id" FROM "Space" WHERE "Id"=:space FOR UPDATE""").SetParameter("space", f.SpaceId).ListAsync();
        await trash.CreateSQLQuery("""
            UPDATE "Page" SET "IsTrashed"=true, "TrashedAt"=now(), "TrashedById"="OwnerId", "TrashBatchId"=gen_random_uuid() WHERE "Id"=:page
            """).SetParameter("page", parent).ExecuteUpdateAsync();
        using var late = f.Host.OpenSession();
        using var lateTransaction = late.BeginTransaction();
        var latePid = await BackendPidAsync(late);
        await late.SaveAsync(Child(f, parent));
        var refused = late.FlushAsync();
        await WaitForLockWaitAsync(f.Host.ConnectionString, latePid, refused);
        await trashTransaction.CommitAsync();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => refused);
        Assert.Contains("Parent must be active in the same space", Messages(error));
    }

    private static Page Child(PageOrganizationFixture f, Guid parent)
    {
        return new Page { Id = Guid.NewGuid(), SpaceId = f.SpaceId, ParentId = parent, OwnerId = f.Writer.Id };
    }

    private static string Messages(Exception? error)
    {
        var messages = new List<string>();
        for (; error is not null; error = error.InnerException)
        {
            messages.Add(error.Message);
        }
        return string.Join(" | ", messages);
    }

    private static Task<int> BackendPidAsync(NHibernate.ISession session) =>
        session.CreateSQLQuery("SELECT pg_backend_pid()").UniqueResultAsync<int>();

    /// <summary>Waits until that backend waits for a lock: other tests on the shared database wait for locks of their own.</summary>
    private static async Task WaitForLockWaitAsync(string connectionString, int pid, Task writer)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        for (var attempt = 0; attempt < 600 && !writer.IsCompleted; attempt++)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE pid=$1 AND wait_event_type='Lock'", connection)
            {
                Parameters = { new() { Value = pid } },
            };
            if ((long)(await command.ExecuteScalarAsync())! >= 1)
            {
                return;
            }
            await Task.Delay(50);
        }
    }
}
