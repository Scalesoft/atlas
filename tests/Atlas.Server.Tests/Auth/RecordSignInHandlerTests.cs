using NHibernate;
using Atlas.Server.Features.Auth.Commands;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Auth;

public sealed class RecordSignInHandlerTests
{
    [PostgreSqlFact]
    public async Task Sign_in_records_a_fact_for_the_current_stamp_and_rejects_a_rotated_one()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_record_signin_");
        var user = await host.SeedUserAsync("admin");
        var at = await host.InvokeAsync<DateTime>(new RecordSignIn(user.Id, user.SecurityStamp, "builtin"));
        using (var session = host.OpenSession())
        {
            var fact = Assert.Single(await session.QueryOver<UserSignIn>().Where(x => x.UserId == user.Id).ListAsync());
            Assert.Equal("builtin", fact.AuthenticationMethod);
            Assert.Equal(at, fact.AuthenticatedAt);
        }
        var stale = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<DateTime>(new RecordSignIn(user.Id, "rotated-stamp", "builtin")));
        Assert.Equal(401, stale.Status);
        Assert.Equal("Přihlášení vypršelo.", stale.Message);
        var missing = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<DateTime>(new RecordSignIn(Guid.NewGuid(), user.SecurityStamp, "builtin")));
        Assert.Equal(401, missing.Status);
    }

    [PostgreSqlFact]
    public async Task Deactivated_account_cannot_record_a_sign_in()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_record_signin_inactive_");
        var user = await host.SeedUserAsync("admin");
        await host.SeedAsync(async session =>
        {
            var account = await session.GetAsync<UserAccount>(user.Id);
            account.IsActive = false;
            await AccountHistory.RecordAsync(session, account, null, CancellationToken.None);
        });
        var error = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<DateTime>(new RecordSignIn(user.Id, user.SecurityStamp, "builtin")));
        Assert.Equal(401, error.Status);
    }
}
