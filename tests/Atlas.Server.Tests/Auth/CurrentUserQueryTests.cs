using Atlas.Server.Features.Auth.Contracts;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Auth.Queries;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Auth;

public sealed class CurrentUserQueryTests
{
    [PostgreSqlFact]
    public async Task Current_user_reports_the_saved_appearance_and_falls_back_to_the_default()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_current_user_");
        var user = await host.SeedUserAsync("admin", isWikiAdmin: true);
        host.Actor.Current = user;
        var withoutPreference = await host.InvokeAsync<UserDto>(new GetCurrentUser());
        Assert.Equal(user.Id, withoutPreference.Id);
        Assert.Equal(user.Username, withoutPreference.Username);
        Assert.True(withoutPreference.IsWikiAdmin);
        Assert.Equal(Appearance.Default, withoutPreference.Appearance);
        Assert.True(withoutPreference.Features.EditorAssist);
        await host.SeedAsync(session => session.SaveAsync(new UserAppearancePreference { UserId = user.Id, Layout = "islands", Accent = "green", Mode = "dark" }));
        var withPreference = await host.InvokeAsync<UserDto>(new GetCurrentUser());
        Assert.Equal(new Appearance("islands", "green", "dark"), withPreference.Appearance);
    }

    [PostgreSqlFact]
    public async Task Anonymous_caller_is_rejected()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_current_user_anonymous_");
        host.Actor.Current = null;
        var error = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<UserDto>(new GetCurrentUser()));
        Assert.Equal(401, error.Status);
        Assert.Equal("Přihlaste se.", error.Message);
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Rotated_security_stamp_ends_the_session()
    {
        var accessor = new FixedHttpContextAccessor();
        await using var host = await AtlasTestHost.CreateAsync("atlas_current_user_rotated_", services =>
        {
            services.AddSingleton<IHttpContextAccessor>(accessor);
            services.AddScoped<ICurrentActor, HttpCurrentActor>();
        });
        var user = await host.SeedUserAsync("admin");
        accessor.SignIn(user.Id, user.SecurityStamp);
        Assert.Equal(user.Id, (await host.InvokeAsync<UserDto>(new GetCurrentUser())).Id);
        await host.ExecuteAsync("UPDATE \"UserAccount\" SET \"SecurityStamp\"='rotated' WHERE \"Id\"=:id", user.Id);
        var error = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<UserDto>(new GetCurrentUser()));
        Assert.Equal(401, error.Status);
        Assert.Equal("Přihlášení vypršelo.", error.Message);
    }
}
