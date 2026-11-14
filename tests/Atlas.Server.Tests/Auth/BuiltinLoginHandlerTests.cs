using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NHibernate;
using Atlas.Server.Features.Auth;
using Atlas.Server.Features.Auth.Builtin.Commands;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Auth;

public sealed class BuiltinLoginHandlerTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Password_login_accepts_the_owner_and_refuses_everyone_else_with_one_message()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_builtin_login_", services => services.AddAuth());
        var hasher = host.Services.GetRequiredService<IPasswordHasher<UserAccount>>();
        var user = await host.SeedUserAsync("admin", isWikiAdmin: true);
        await host.SeedAsync(session => session.SaveAsync(new BuiltinCredential { UserId = user.Id, PasswordHash = hasher.HashPassword(user, "correct horse battery") }));
        var signedIn = await host.InvokeAsync<UserAccount>(new BuiltinLogin("admin", "correct horse battery"));
        Assert.Equal(user.Id, signedIn.Id);
        foreach (var attempt in new[] { new BuiltinLogin("admin", "wrong password here"), new BuiltinLogin("nobody", "correct horse battery") })
        {
            var error = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<UserAccount>(attempt));
            Assert.Equal(401, error.Status);
            Assert.Equal("Neplatné přihlašovací údaje.", error.Message);
        }
    }

    [PostgreSqlFact]
    public async Task Deactivated_account_and_credential_without_password_cannot_sign_in()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_builtin_login_denied_", services => services.AddAuth());
        var hasher = host.Services.GetRequiredService<IPasswordHasher<UserAccount>>();
        var disabled = await host.SeedUserAsync("disabled");
        var invited = await host.SeedUserAsync("invited");
        await host.SeedAsync(async session =>
        {
            var account = await session.GetAsync<UserAccount>(disabled.Id);
            account.IsActive = false;
            await AccountHistory.RecordAsync(session, account, null, CancellationToken.None);
            await session.SaveAsync(new BuiltinCredential { UserId = disabled.Id, PasswordHash = hasher.HashPassword(disabled, "correct horse battery") });
            await session.SaveAsync(new BuiltinCredential { UserId = invited.Id, PasswordHash = null });
        });
        foreach (var attempt in new[] { new BuiltinLogin("disabled", "correct horse battery"), new BuiltinLogin("invited", "correct horse battery") })
        {
            Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<UserAccount>(attempt))).Status);
        }
    }
}
