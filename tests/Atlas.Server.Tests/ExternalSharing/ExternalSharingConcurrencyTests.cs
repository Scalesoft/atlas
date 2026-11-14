using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Atlas.Server.Features.ExternalSharing.Contracts;
using Atlas.Server.Features.ExternalSharing.Queries;
using Xunit;

namespace Atlas.Server.Tests.ExternalSharing;

public sealed class ExternalSharingConcurrencyTests
{
    [PostgreSqlFact]
    public Task Policy_read_serializes_with_concurrent_space_grant_changes() => VerifyReadAsync(false);

    [PostgreSqlFact]
    public Task Page_link_list_serializes_with_concurrent_space_grant_changes() => VerifyReadAsync(true);

    private static async Task VerifyReadAsync(bool list)
    {
        var gate = new ReadGate();
        await using var fixture = await SharingFixture.CreateAsync(services => services.AddScoped<IAtlasLocks>(provider =>
            new PausingLocks(new AtlasLocks(provider.GetRequiredService<NHibernate.ISession>()), gate)));
        await fixture.Enable();
        gate.Armed = true;
        // The gate pauses the actual handler under its transaction, before the role query. Every
        // database lock still runs normally; this only makes the competing write deterministic.
        var read = Task.Run(async () =>
        {
            if (list) await fixture.Host.InvokeAsync<ShareLinksDto>(new ListShareLinks(fixture.SpaceId, fixture.PageId));
            else await fixture.Host.InvokeAsync<SharingPolicyDto>(new GetSharingPolicy(fixture.SpaceId));
        });
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var sql = new NpgsqlConnection(fixture.Host.ConnectionString);
            await sql.OpenAsync();
            await using var transaction = await sql.BeginTransactionAsync();
            // SetSpaceGrant acquires this exact row lock before changing any grant. PostgreSQL's
            // lock timeout is an observable lock-conflict assertion, not a scheduling sleep.
            await using var timeout = new NpgsqlCommand("SET LOCAL lock_timeout='100ms'", sql, transaction);
            await timeout.ExecuteNonQueryAsync();
            await using var grantWrite = new NpgsqlCommand("SELECT \"Id\" FROM \"Space\" WHERE \"Id\"=@id FOR UPDATE", sql, transaction);
            grantWrite.Parameters.AddWithValue("id", fixture.SpaceId);
            var conflict = await Assert.ThrowsAsync<PostgresException>(() => grantWrite.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, conflict.SqlState);
            await transaction.RollbackAsync();
        }
        finally
        {
            gate.Release.TrySetResult();
            await read.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class ReadGate
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PausingLocks(IAtlasLocks inner, ReadGate gate) : IAtlasLocks
    {
        public Task AcquireContentSharedAsync(CancellationToken ct) => inner.AcquireContentSharedAsync(ct);
        public Task AcquireContentExclusiveAsync(CancellationToken ct) => inner.AcquireContentExclusiveAsync(ct);
        public Task AcquireAdvisoryAsync(long key, CancellationToken ct) => inner.AcquireAdvisoryAsync(key, ct);
        public Task AcquireAdvisoryAsync(string key, CancellationToken ct) => inner.AcquireAdvisoryAsync(key, ct);
        public Task LockSpaceSharedAsync(Guid id, CancellationToken ct) => inner.LockSpaceSharedAsync(id, ct);
        public Task<bool> LockSpaceForUpdateAsync(Guid id, CancellationToken ct) => inner.LockSpaceForUpdateAsync(id, ct);
        public Task<bool> LockPageSharedAsync(Guid id, CancellationToken ct) => inner.LockPageSharedAsync(id, ct);
        public Task<bool> LockPageForUpdateAsync(Guid id, CancellationToken ct) => inner.LockPageForUpdateAsync(id, ct);
        public Task<bool> LockAccountForUpdateAsync(Guid id, CancellationToken ct) => inner.LockAccountForUpdateAsync(id, ct);
        public Task SetActorAsync(Guid id, CancellationToken ct, Guid? browserSessionId = null) => inner.SetActorAsync(id, ct, browserSessionId);
        public Task<IReadOnlyList<Guid>> LockGroupsForKeyShareAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => inner.LockGroupsForKeyShareAsync(ids, ct);
        public Task<IReadOnlyList<Guid>> LockPagesForUpdateAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => inner.LockPagesForUpdateAsync(ids, ct);
        public Task<(long Version, int ProxyHopLimit)> ReadSecurityConfigurationSharedAsync(CancellationToken ct) => inner.ReadSecurityConfigurationSharedAsync(ct);
        public Task<Page?> ReadPageSharedAsync(Guid id, CancellationToken ct) => inner.ReadPageSharedAsync(id, ct);
        public async Task LockAccountSharedAsync(Guid id, CancellationToken ct)
        {
            await inner.LockAccountSharedAsync(id, ct);
            await PauseAsync(ct);
        }
        public async Task<UserAccount?> ReadAccountSharedAsync(Guid id, CancellationToken ct)
        {
            var account = await inner.ReadAccountSharedAsync(id, ct);
            await PauseAsync(ct);
            return account;
        }
        private async Task PauseAsync(CancellationToken ct)
        {
            if (!gate.Armed) return;
            gate.Entered.TrySetResult();
            await gate.Release.Task.WaitAsync(ct);
        }
    }
}
