using Microsoft.Extensions.DependencyInjection;
using NHibernate;
using Atlas.Server.Features.Auth;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Auth.Recovery;
using Atlas.Server.Features.Setup.Commands;
using Atlas.Server.Features.Setup.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Setup;

public sealed class SetupHandlerTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Setup_creates_the_workspace_admin_credential_appearance_onboarding_and_first_space()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_setup_", services => services.AddAuth());
        var bootstrap = await host.Services.GetRequiredService<ServerAccessTokens>().EnsureBootstrapAsync();
        var admin = await host.InvokeAsync<UserAccount>(new CompleteSetup(bootstrap!, " Acme Wiki ", " Admin ", " Administrator ", "safe long test passphrase"));
        Assert.Equal("admin", admin.Username);
        Assert.Equal("Administrator", admin.DisplayName);
        Assert.True(admin.IsWikiAdmin);
        using var session = host.OpenSession();
        Assert.Equal("Acme Wiki", (await session.GetAsync<Workspace>(1)).Name);
        Assert.NotNull((await session.GetAsync<BuiltinCredential>(admin.Id)).PasswordHash);
        var appearance = Assert.Single(await session.QueryOver<UserAppearancePreference>().ListAsync());
        Assert.Equal(Appearance.Default, appearance.Appearance);
        var onboarding = Assert.Single(await session.QueryOver<ModuleOnboarding>().ListAsync());
        Assert.Equal(1, onboarding.WorkspaceId);
        Assert.Null(onboarding.ActiveTo);
        Assert.Null(onboarding.CompletedById);
        Assert.Equal(DateTimeKind.Utc, onboarding.ActiveFrom.Kind);
        var space = Assert.Single(await session.QueryOver<Space>().ListAsync());
        Assert.Equal("Začínáme", space.Name);
        Assert.Equal("book-open", space.Icon);
        Assert.Equal("blue", space.Color);
        var grant = Assert.Single(await session.QueryOver<SpaceGrant>().ListAsync());
        Assert.Equal(space.Id, grant.SpaceId);
        Assert.Equal(admin.Id, grant.UserId);
        Assert.Equal((int)SpaceRole.Admin, grant.Role);
        Assert.Equal(1, await session.RelationRowsAsync("UserAccount"));
        Assert.Equal(1, await session.RelationRowsAsync("Space"));
        Assert.Equal(1, await session.RelationRowsAsync("SpaceGrant"));
        Assert.Equal(0, await session.RelationRowsAsync("GroupMember"));
        // The trigger records the founding revision with the actor set by SetActorAsync.
        Assert.Equal(1L, await session.CreateSQLQuery("SELECT COUNT(*) FROM \"UserAccountRevision\" WHERE \"UserId\"=:id").SetParameter("id", admin.Id).UniqueResultAsync<long>());
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Setup_rejects_a_wrong_token_a_short_password_and_a_second_run()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_setup_guard_", services => services.AddAuth());
        var bootstrap = await host.Services.GetRequiredService<ServerAccessTokens>().EnsureBootstrapAsync();
        var wrong = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<UserAccount>(new CompleteSetup(new string('x', 64), "Wiki", "admin", "Admin", "safe long test passphrase")));
        Assert.Equal(403, wrong.Status);
        var short_ = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<UserAccount>(new CompleteSetup(bootstrap!, "Wiki", "admin", "Admin", "short")));
        Assert.Equal(400, short_.Status);
        Assert.Equal("Heslo musí mít 12 až 256 znaků.", short_.Message);
        await host.InvokeAsync<UserAccount>(new CompleteSetup(bootstrap!, "Wiki", "admin", "Admin", "safe long test passphrase"));
        var again = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<UserAccount>(new CompleteSetup(bootstrap!, "Wiki", "second", "Second", "safe long test passphrase")));
        Assert.Equal(409, again.Status);
        Assert.Equal("Wiki již byla nastavena.", again.Message);
        using var session = host.OpenSession();
        Assert.Equal(1, await session.QueryOver<UserAccount>().RowCountAsync());
    }

    [Fact]
    public void A_setup_command_never_formats_the_password_for_logs()
    {
        var command = new CompleteSetup("token", "Wiki", "admin", "Admin", "sensitive-test-password");
        Assert.DoesNotContain(command.Password, command.ToString(), StringComparison.Ordinal);
    }
}
