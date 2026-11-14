using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Administration;

public sealed class AdministrationProjectionTests
{
    [PostgreSqlFact]
    public async Task Projections_expose_current_rows_with_resolved_names_and_UTC_versions()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_admin_views_");
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var member = await host.SeedUserAsync("member");
        var spaceId = await host.SeedSpaceAsync("Docs", (admin, SpaceRole.Admin));
        var groupId = Guid.NewGuid();
        await host.SeedAsync(async session =>
        {
            var engineering = new UserGroup { Id = groupId, Name = "Engineering" };
            await session.SaveAsync(engineering);
            await GroupHistory.RecordAsync(session, engineering, null, CancellationToken.None);
            await session.SaveAsync(new GroupMember { Id = Guid.NewGuid(), GroupId = groupId, UserId = member.Id, ActiveFrom = DateTime.UtcNow });
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = spaceId, GroupId = groupId, Role = 2, ActiveFrom = DateTime.UtcNow });
        });

        using var session = host.OpenSession();
        var user = await session.QueryOver<AdministrationUserView>().Where(x => x.Id == member.Id).SingleOrDefaultAsync();
        Assert.Equal(member.Username, user.Username);
        Assert.False(user.HasBuiltinCredential);
        Assert.Equal(DateTimeKind.Utc, user.ActiveFrom.Kind);
        Assert.Equal("external", user.ToSummary().Source);
        Assert.Equal("none", user.ToSummary().CredentialState);

        var group = await session.QueryOver<AdministrationGroupView>().Where(x => x.Id == groupId).SingleOrDefaultAsync();
        Assert.Equal("Engineering", group.Name);

        var membership = Assert.Single(await session.QueryOver<GroupMemberView>().Where(x => x.GroupId == groupId).ListAsync());
        Assert.Equal(member.Id, membership.UserId);
        Assert.Equal(member.Username, membership.Username);
        Assert.Equal(member.Id, membership.ToSummary().Id);
        Assert.Equal(DateTimeKind.Utc, membership.ActiveFrom.Kind);

        var grants = await session.QueryOver<SpaceGrantView>().Where(x => x.SpaceId == spaceId).ListAsync();
        Assert.Equal(2, grants.Count);
        Assert.Equal("Engineering", Assert.Single(grants, x => x.GroupId == groupId).Name);
        Assert.Equal("Writer", Assert.Single(grants, x => x.GroupId == groupId).ToSummary().Role);
        Assert.Equal(admin.DisplayName, Assert.Single(grants, x => x.UserId == admin.Id).Name);

        var profile = await session.QueryOver<ProfileAccountView>().Where(x => x.Id == member.Id).SingleOrDefaultAsync();
        Assert.Equal(member.DisplayName, profile.DisplayName);
        Assert.Null(profile.Email);
        Assert.False(profile.HasBuiltinCredential);
        Assert.False(profile.HasExternalIdentity);
        Assert.Null(profile.LastSignInAt);
    }
}
