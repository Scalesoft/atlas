using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class ContentAccessTests
{
    private const string NotFound = "Prostor nebyl nalezen.";
    private const string NoAccess = "Prostor nebyl nalezen nebo k němu nemáte přístup.";
    private const string Expired = "Přihlášení vypršelo.";
    private const string NoWrite = "K úpravám obsahu potřebujete v prostoru oprávnění k zápisu.";

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Effective_role_comes_from_user_or_group_grants_and_wiki_admin_bypasses()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_access_");
        var admin = await host.SeedUserAsync("a", isWikiAdmin: true);
        var reader = await host.SeedUserAsync("r");
        var member = await host.SeedUserAsync("g");
        var outsider = await host.SeedUserAsync("o");
        // The member also holds a direct Reader grant: the effective role is the maximum over both sources.
        var spaceId = await host.SeedSpaceAsync("S", (reader, SpaceRole.Reader), (member, SpaceRole.Reader));
        var groupId = await host.SeedGroupAsync("G", member);
        await host.SeedAsync(async session =>
        {
            // A closed Admin grant in the past must not count.
            await session.CreateSQLQuery("INSERT INTO \"SpaceGrant\"(\"Id\",\"SpaceId\",\"UserId\",\"Role\",\"ActiveFrom\",\"ActiveTo\") VALUES (gen_random_uuid(),:s,:u,3,now() - interval '2 day',now() - interval '1 day')")
                .SetParameter("s", spaceId)
                .SetParameter("u", reader.Id)
                .ExecuteUpdateAsync();
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = spaceId, GroupId = groupId, Role = 2 });
        });

        host.Actor.Current = admin;
        Assert.Equal(SpaceRole.Admin, await host.InvokeAsync<SpaceRole>(new ProbeAccess(spaceId, spaceId, SpaceRole.Admin)));
        await Denied(404, NotFound, () => host.InvokeAsync<SpaceRole>(new ProbeAccess(spaceId, Guid.NewGuid(), SpaceRole.Reader)));
        host.Actor.Current = reader;
        Assert.Equal(SpaceRole.Reader, await host.InvokeAsync<SpaceRole>(new ProbeAccess(spaceId, spaceId, SpaceRole.Reader)));
        // A reader knows the space, so it is told the missing right (review S3).
        await Denied(403, NoWrite, () => host.InvokeAsync<SpaceRole>(new ProbeAccess(spaceId, spaceId, SpaceRole.Writer)));
        host.Actor.Current = member;
        Assert.Equal(SpaceRole.Writer, await host.InvokeAsync<SpaceRole>(new ProbeAccess(null, spaceId, SpaceRole.Writer)));
        host.Actor.Current = outsider;
        await Denied(404, NoAccess, () => host.InvokeAsync<SpaceRole>(new ProbeAccess(null, spaceId, SpaceRole.Reader)));
        await Denied(404, NoAccess, () => host.InvokeAsync<SpaceRole>(new ProbeAccess(Guid.NewGuid(), spaceId, SpaceRole.Reader)));

        await host.ExecuteAsync("UPDATE \"UserAccount\" SET \"SecurityStamp\"='revoked' WHERE \"Id\"=:id", reader.Id);
        host.Actor.Current = reader;
        await Denied(401, Expired, () => host.InvokeAsync<SpaceRole>(new ProbeAccess(spaceId, spaceId, SpaceRole.Reader)));
    }

    [PostgreSqlFact]
    public async Task Lock_actor_rereads_the_account_under_the_lock_even_when_the_session_already_cached_it()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_access_");
        var user = await host.SeedUserAsync("c");
        host.Actor.Current = user;
        await Denied(401, Expired, () => host.InvokeAsync<Guid>(new ProbeStaleActor(user.Id)));
    }

    [PostgreSqlFact]
    public async Task Wiki_administrator_does_not_authorize_an_unrecognized_content_requirement()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_access_unknown_");
        host.Actor.Current = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var spaceId = await host.SeedSpaceAsync("S");
        // Still refused; the administrator sees every space, so it is told 403, not 404 (review S3).
        await Denied(403, NoWrite, () => host.InvokeAsync<SpaceRole>(new ProbeAccess(spaceId, spaceId, (SpaceRole)999)));
        Assert.Equal(SpaceRole.Admin, await host.InvokeAsync<SpaceRole>(new ProbeAccess(spaceId, spaceId, SpaceRole.Admin)));
    }

    private static async Task Denied(int status, string message, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(status, error.Status);
        Assert.Equal(message, error.Message);
    }
}
