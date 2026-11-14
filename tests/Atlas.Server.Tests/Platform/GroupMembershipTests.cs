using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class GroupMembershipTests
{
    [PostgreSqlFact]
    public async Task Current_memberships_and_grants_are_queryable_by_ActiveTo()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_membership_");
        var user = await host.SeedUserAsync("m");
        var groupId = await host.SeedGroupAsync("G", user);
        var spaceId = await host.SeedSpaceAsync("S");
        await host.SeedAsync(async session =>
        {
            // A closed membership in the past must not count as current.
            await session.CreateSQLQuery("INSERT INTO \"GroupMember\"(\"Id\",\"GroupId\",\"UserId\",\"ActiveFrom\",\"ActiveTo\") VALUES (gen_random_uuid(),:g,:u,now() - interval '2 day',now() - interval '1 day')")
                .SetParameter("g", groupId)
                .SetParameter("u", user.Id)
                .ExecuteUpdateAsync();
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = spaceId, GroupId = groupId, Role = 2 });
        });
        using var check = host.OpenSession();
        var current = await check.QueryOver<GroupMembership>().Where(x => x.UserId == user.Id && x.ActiveTo == null).ListAsync();
        Assert.Single(current);
        Assert.Equal(groupId, current[0].GroupId);
        var grant = await check.QueryOver<SpaceGrant>().Where(x => x.SpaceId == spaceId && x.ActiveTo == null).SingleOrDefaultAsync();
        Assert.NotNull(grant);
        Assert.Equal(2, grant.Role);
        Assert.True((await check.CreateSQLQuery("SELECT \"ActiveFrom\" FROM \"SpaceGrant\" WHERE \"Id\"=:id").SetParameter("id", grant.Id).UniqueResultAsync<DateTime>()) > DateTime.UtcNow.AddMinutes(-1));
    }
}
