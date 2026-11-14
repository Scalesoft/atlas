using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class SpaceRolesTests
{
    [PostgreSqlFact]
    public async Task Effective_roles_take_the_maximum_of_direct_and_group_grants()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_space_roles_");
        var member = await host.SeedUserAsync("member");
        var groupId = await host.SeedGroupAsync("Editors", member);
        var direct = await host.SeedSpaceAsync("Direct", (member, SpaceRole.Reader));
        var shared = await host.SeedSpaceAsync("Shared");
        var hidden = await host.SeedSpaceAsync("Hidden");
        await host.SeedAsync(async session =>
        {
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = direct, GroupId = groupId, Role = (int)SpaceRole.Admin });
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = shared, GroupId = groupId, Role = (int)SpaceRole.Writer });
        });
        using var session = host.OpenSession();
        var roles = new SpaceRoles(session);
        var all = await roles.EffectiveAsync(member, CancellationToken.None);
        Assert.Equal(SpaceRole.Admin, all[direct]);
        Assert.Equal(SpaceRole.Writer, all[shared]);
        Assert.False(all.ContainsKey(hidden));
        Assert.Equal(SpaceRole.Writer, await roles.EffectiveAsync(member, shared, CancellationToken.None));
        Assert.Null(await roles.EffectiveAsync(member, hidden, CancellationToken.None));
    }
}
