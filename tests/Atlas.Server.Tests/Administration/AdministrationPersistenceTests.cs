using NHibernate.Exceptions;
using Npgsql;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Administration;

public sealed class AdministrationPersistenceTests
{
    [PostgreSqlFact]
    public async Task Dirty_checked_close_passes_the_immutability_guard_and_a_reopen_chains_on_the_boundary()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_admin_persistence_");
        var user = await host.SeedUserAsync("member");
        var groupId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        await host.SeedAsync(async session =>
        {
            await session.SaveAsync(new UserGroup { Id = groupId, Name = "Engineering" });
            await session.SaveAsync(new GroupMember { Id = memberId, GroupId = groupId, UserId = user.Id, ActiveFrom = DateTime.UtcNow });
        });

        DateTime boundary;
        using (var session = host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var current = await session.GetAsync<GroupMember>(memberId);
            boundary = Temporal.NextBoundary(current.ActiveFrom, DateTime.UtcNow);
            current.ActiveTo = boundary;
            await transaction.CommitAsync();
        }

        using (var session = host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var reopened = Temporal.NextBoundary(boundary, DateTime.UtcNow);
            await session.SaveAsync(new GroupMember { Id = Guid.NewGuid(), GroupId = groupId, UserId = user.Id, ActiveFrom = reopened });
            await transaction.CommitAsync();
        }

        using (var session = host.OpenSession())
        {
            var rows = await session.QueryOver<GroupMember>().Where(x => x.UserId == user.Id).OrderBy(x => x.ActiveFrom).Asc.ListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(boundary, rows[0].ActiveTo);
            Assert.Null(rows[1].ActiveTo);
            Assert.True(rows[1].ActiveFrom >= boundary);
        }

        // The guard is value based: only ActiveTo may move, and only away from NULL (IntegrityRules).
        await IntegrityRefusal.AssertAsync(host, IntegrityRules.HistoryImmutable,
            async session => (await session.GetAsync<GroupMember>(memberId))!.ActiveTo = DateTime.UtcNow);
    }

    [PostgreSqlFact]
    public async Task Space_grant_close_must_flush_before_the_replacement_insert()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_admin_grants_");
        var user = await host.SeedUserAsync("member");
        // SeedSpaceAsync writes a grant without touching ActiveFrom. The column is mapped, so the database
        // default never fires: only the entity initializer keeps that row off 0001-01-01, and the close below
        // needs a real boundary to chain on.
        var spaceId = await host.SeedSpaceAsync("Docs", (user, SpaceRole.Reader));

        Guid grantId;
        using (var probe = host.OpenSession())
        {
            var seeded = await probe.QueryOver<SpaceGrant>().Where(x => x.UserId == user.Id).SingleOrDefaultAsync();
            grantId = seeded.Id;
            Assert.Equal(DateTimeKind.Utc, seeded.ActiveFrom.Kind);
            Assert.True(Math.Abs((DateTime.UtcNow - seeded.ActiveFrom).TotalMinutes) < 1);
        }

        using (var session = host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var current = await session.GetAsync<SpaceGrant>(grantId);
            var boundary = Temporal.NextBoundary(current.ActiveFrom, DateTime.UtcNow);
            current.ActiveTo = boundary;
            // NHibernate orders inserts before updates; without this flush the partial unique index sees two current grants.
            await session.FlushAsync();
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = spaceId, UserId = user.Id, Role = 2, ActiveFrom = boundary });
            await transaction.CommitAsync();
        }

        using var check = host.OpenSession();
        var currentGrant = await check.QueryOver<SpaceGrant>().Where(x => x.UserId == user.Id && x.ActiveTo == null).SingleOrDefaultAsync();
        Assert.Equal(2, currentGrant.Role);
        Assert.Equal(2, await check.QueryOver<SpaceGrant>().Where(x => x.UserId == user.Id).RowCountAsync());
    }

    [PostgreSqlFact]
    public async Task Group_insert_and_rename_write_attributed_revisions()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_admin_groups_");
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var groupId = Guid.NewGuid();
        host.Actor.Current = admin;
        await host.InvokeAsync(new SeedGroupRevision(groupId, "Engineering", "Development"));

        using var session = host.OpenSession();
        var revisions = await session.CreateSQLQuery(
                "SELECT \"Name\",\"ChangedBy\",\"ActiveTo\" FROM \"UserGroupRevision\" WHERE \"GroupId\"=:id ORDER BY \"ActiveFrom\"")
            .SetParameter("id", groupId).ListAsync<object[]>();
        Assert.Equal(2, revisions.Count);
        Assert.Equal("Engineering", revisions[0][0]);
        Assert.Equal(admin.Id, revisions[0][1]);
        Assert.NotNull(revisions[0][2]);
        Assert.Equal("Development", revisions[1][0]);
        Assert.Null(revisions[1][2]);
    }

    [PostgreSqlFact]
    public async Task A_membership_written_with_the_database_clock_closes_through_the_dirty_check_path()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_admin_dbclock_");
        var user = await host.SeedUserAsync("member");
        // SeedGroupAsync writes ActiveFrom with now(), at the microsecond precision of the column; the entity round trip
        // must not shift it, or the guard would see a second column change and reject the close with 23514.
        var groupId = await host.SeedGroupAsync("Engineering", user);

        Guid memberId;
        using (var session = host.OpenSession())
        {
            memberId = (await session.QueryOver<GroupMember>().Where(x => x.UserId == user.Id).SingleOrDefaultAsync()).Id;
        }

        using (var session = host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var current = await session.GetAsync<GroupMember>(memberId);
            current.ActiveTo = Temporal.NextBoundary(current.ActiveFrom, DateTime.UtcNow);
            await transaction.CommitAsync();
        }

        using var check = host.OpenSession();
        var closed = await check.GetAsync<GroupMember>(memberId);
        Assert.Equal(groupId, closed.GroupId);
        Assert.NotNull(closed.ActiveTo);
        Assert.True(closed.ActiveTo > closed.ActiveFrom);
    }
}
