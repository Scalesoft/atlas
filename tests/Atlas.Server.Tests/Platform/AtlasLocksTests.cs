using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class AtlasLocksTests
{
    [PostgreSqlFact]
    public async Task Locks_require_transaction_report_missing_space_and_set_actor()
    {
        await using var schema = await DisposableSchema.CreateMigratedAsync("atlas_locks_");
        using var database = new AtlasDatabase(schema.Configuration, NullLogger<AtlasDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        using var session = database.OpenSession();
        IAtlasLocks locks = new AtlasLocks(session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => locks.AcquireContentSharedAsync(CancellationToken.None));
        using var transaction = session.BeginTransaction();
        await locks.AcquireContentSharedAsync(CancellationToken.None);
        Assert.Equal(1L, await AdvisoryLockCountAsync(session, "ShareLock"));
        var missing = await Assert.ThrowsAsync<ApiException>(() => locks.LockSpaceSharedAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(404, missing.Status);
        Assert.Equal("Prostor nebyl nalezen nebo k němu nemáte přístup.", missing.Message);
        var actor = Guid.NewGuid();
        await locks.SetActorAsync(actor, CancellationToken.None);
        // The actor travels in TransactionActor; no session variable is set, and no function of the schema reads one.
        Assert.Equal(actor, TransactionActor.Of(session.GetSessionImplementation()));
        Assert.Equal(0L, await session.CreateSQLQuery("SELECT count(*) FROM pg_proc WHERE prosrc LIKE '%scaleatlas.actor_id%'").UniqueResultAsync<long>());
        Assert.Empty(await locks.LockGroupsForKeyShareAsync([Guid.NewGuid()], CancellationToken.None));
        await transaction.CommitAsync();
    }

    [PostgreSqlFact]
    public async Task Only_query_transactions_commit_without_waiting_for_the_wal_flush()
    {
        await using var schema = await DisposableSchema.CreateMigratedAsync("atlas_locks_");
        using var database = new AtlasDatabase(schema.Configuration, NullLogger<AtlasDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        using var session = database.OpenSession();
        var intent = new TransactionIntent();
        IAtlasLocks locks = new AtlasLocks(session, intent);

        intent.Begin(new Atlas.Server.Features.Pages.Queries.GetPage(Guid.NewGuid()));
        Assert.True(intent.IsRead);
        using (var read = session.BeginTransaction())
        {
            await locks.SetActorAsync(Guid.NewGuid(), CancellationToken.None);
            Assert.Equal("off", await session.CreateSQLQuery("SHOW synchronous_commit").UniqueResultAsync<string>());
            await read.CommitAsync();
        }
        // SET LOCAL semantics: the next transaction on the same connection is durable again.
        intent.Begin(new Atlas.Server.Features.Pages.Commands.PublishPage(Guid.NewGuid(), new(1)));
        Assert.False(intent.IsRead);
        using var write = session.BeginTransaction();
        await locks.SetActorAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Equal("on", await session.CreateSQLQuery("SHOW synchronous_commit").UniqueResultAsync<string>());
        await write.CommitAsync();
    }

    [PostgreSqlFact]
    public async Task Row_locks_report_whether_the_row_exists()
    {
        await using var schema = await DisposableSchema.CreateMigratedAsync("atlas_locks_");
        using var database = new AtlasDatabase(schema.Configuration, NullLogger<AtlasDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        var user = new UserAccount { Id = Guid.NewGuid(), Username = "locks", DisplayName = "Locks" };
        var spaceId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        using (var seed = database.OpenSession())
        using (var seedTransaction = seed.BeginTransaction())
        {
            await seed.SaveAsync(user);
            await seed.SaveAsync(new Space { Id = spaceId, Name = "S" });
            await seed.SaveAsync(new Page { Id = pageId, SpaceId = spaceId, OwnerId = user.Id, Version = 1 });
            await seedTransaction.CommitAsync();
        }
        using var session = database.OpenSession();
        IAtlasLocks locks = new AtlasLocks(session);
        using var transaction = session.BeginTransaction();
        await locks.LockSpaceSharedAsync(spaceId, CancellationToken.None);
        Assert.True(await locks.LockSpaceForUpdateAsync(spaceId, CancellationToken.None));
        Assert.False(await locks.LockSpaceForUpdateAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.True(await locks.LockPageSharedAsync(pageId, CancellationToken.None));
        Assert.False(await locks.LockPageSharedAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.True(await locks.LockPageForUpdateAsync(pageId, CancellationToken.None));
        Assert.False(await locks.LockPageForUpdateAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.True(await locks.LockAccountForUpdateAsync(user.Id, CancellationToken.None));
        Assert.False(await locks.LockAccountForUpdateAsync(Guid.NewGuid(), CancellationToken.None));
        await transaction.CommitAsync();
    }

    [PostgreSqlFact]
    public async Task Advisory_locks_are_registered_for_the_content_key_and_for_long_and_text_keys()
    {
        await using var schema = await DisposableSchema.CreateMigratedAsync("atlas_locks_");
        using var database = new AtlasDatabase(schema.Configuration, NullLogger<AtlasDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        using var session = database.OpenSession();
        IAtlasLocks locks = new AtlasLocks(session);
        using var transaction = session.BeginTransaction();
        // Every distinct advisory key adds one row to pg_locks for this backend; the count proves the
        // statement actually took a lock instead of silently returning a value.
        Assert.Equal(0L, await AdvisoryLockCountAsync(session));
        await locks.AcquireContentExclusiveAsync(CancellationToken.None);
        Assert.Equal(1L, await AdvisoryLockCountAsync(session));
        Assert.Equal(1L, await AdvisoryLockCountAsync(session, "ExclusiveLock"));
        await locks.AcquireAdvisoryAsync(731145099L, CancellationToken.None);
        Assert.Equal(2L, await AdvisoryLockCountAsync(session));
        const string textKey = "https://idp.example.invalid/\nsubject-1";
        await locks.AcquireAdvisoryAsync(textKey, CancellationToken.None);
        Assert.Equal(3L, await AdvisoryLockCountAsync(session));
        // The same text key is re-entrant inside one transaction and adds no further lock object.
        await locks.AcquireAdvisoryAsync(textKey, CancellationToken.None);
        Assert.Equal(3L, await AdvisoryLockCountAsync(session));
        // The text key hashes with seed 812, the value ExternalIdentityService has always used: taking the
        // same key through the long overload must land on the lock object that already exists.
        var legacyKey = await session.CreateSQLQuery("SELECT hashtextextended(:key, 812)").SetParameter("key", textKey).UniqueResultAsync<long>();
        await locks.AcquireAdvisoryAsync(legacyKey, CancellationToken.None);
        Assert.Equal(3L, await AdvisoryLockCountAsync(session));
        await transaction.CommitAsync();
    }

    [PostgreSqlFact]
    public async Task Every_lock_requires_an_active_transaction()
    {
        await using var schema = await DisposableSchema.CreateMigratedAsync("atlas_locks_");
        using var database = new AtlasDatabase(schema.Configuration, NullLogger<AtlasDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        using var session = database.OpenSession();
        IAtlasLocks locks = new AtlasLocks(session);
        var operations = new (string Name, Func<Task> Run)[]
        {
            ("AcquireContentSharedAsync", () => locks.AcquireContentSharedAsync(CancellationToken.None)),
            ("AcquireContentExclusiveAsync", () => locks.AcquireContentExclusiveAsync(CancellationToken.None)),
            ("AcquireAdvisoryAsync(long)", () => locks.AcquireAdvisoryAsync(1L, CancellationToken.None)),
            ("AcquireAdvisoryAsync(string)", () => locks.AcquireAdvisoryAsync("key", CancellationToken.None)),
            ("LockSpaceSharedAsync", () => locks.LockSpaceSharedAsync(Guid.NewGuid(), CancellationToken.None)),
            ("LockSpaceForUpdateAsync", () => locks.LockSpaceForUpdateAsync(Guid.NewGuid(), CancellationToken.None)),
            ("LockPageSharedAsync", () => locks.LockPageSharedAsync(Guid.NewGuid(), CancellationToken.None)),
            ("LockPageForUpdateAsync", () => locks.LockPageForUpdateAsync(Guid.NewGuid(), CancellationToken.None)),
            ("LockAccountSharedAsync", () => locks.LockAccountSharedAsync(Guid.NewGuid(), CancellationToken.None)),
            ("LockAccountForUpdateAsync", () => locks.LockAccountForUpdateAsync(Guid.NewGuid(), CancellationToken.None)),
            ("SetActorAsync", () => locks.SetActorAsync(Guid.NewGuid(), CancellationToken.None)),
            ("LockGroupsForKeyShareAsync", () => locks.LockGroupsForKeyShareAsync([Guid.NewGuid()], CancellationToken.None))
        };
        var failures = new List<string>();
        foreach (var (name, run) in operations)
        {
            try
            {
                await run();
                failures.Add(name + ": no exception");
            }
            catch (InvalidOperationException error) when (error.Message == "Atlas locks require an active transaction.")
            {
            }
            catch (Exception error)
            {
                failures.Add(name + ": " + error.GetType().Name + " " + error.Message);
            }
        }
        Assert.Empty(failures);
    }

    private static async Task<long> AdvisoryLockCountAsync(NHibernate.ISession session, string? mode = null)
    {
        return await session.CreateSQLQuery("SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND pid=pg_backend_pid() AND (:mode IS NULL OR mode=:mode)")
            .SetParameter("mode", mode, NHibernate.NHibernateUtil.String)
            .UniqueResultAsync<long>();
    }
}
