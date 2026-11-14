using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NHibernate;
using Atlas.Server.Features.Auth.Commands;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Auth;

public sealed class AppearanceHandlerTests
{
    [PostgreSqlFact]
    public async Task Every_color_style_can_be_saved_and_reloaded_without_losing_history()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_color_styles_");
        var user = await host.SeedUserAsync("reader");
        host.Actor.Current = user;
        await host.SeedAsync(session => session.SaveAsync(new UserAppearancePreference { UserId = user.Id }));
        foreach (var accent in new[] { "blue", "green", "red", "ocean", "sunset", "indigo", "violet", "amber" })
        foreach (var mode in new[] { "light", "dark" })
        {
            // The database constraint knows every accent; the reduced-motion choice travels with the rest.
            var expected = new Appearance("islands", accent, mode, mode == "dark" ? "reduce" : "system");
            Assert.Equal(expected, await host.InvokeAsync<Appearance>(new UpdateAppearance(expected)));
            using var read = host.OpenSession();
            var current = await read.QueryOver<UserAppearancePreference>()
                .Where(p => p.UserId == user.Id && p.ActiveTo == null).SingleOrDefaultAsync();
            Assert.Equal(expected, current.Appearance);
        }
        using var session = host.OpenSession();
        var history = (await session.QueryOver<UserAppearancePreference>()
            .Where(p => p.UserId == user.Id).OrderBy(p => p.ActiveFrom).Asc.ListAsync()).ToArray();
        Assert.Equal(17, history.Length);
        Assert.Equal(Appearance.Default, history[0].Appearance);
        Assert.Single(history, p => p.ActiveTo is null);
        for (var i = 1; i < history.Length; i++) Assert.Equal(history[i - 1].ActiveTo, history[i].ActiveFrom);
    }

    [PostgreSqlFact]
    public async Task Concurrent_profile_updates_preserve_history_and_one_current_UTC_interval()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_appearance_");
        var user = await host.SeedUserAsync("admin", isWikiAdmin: true);
        host.Actor.Current = user;
        await host.SeedAsync(session => session.SaveAsync(new UserAppearancePreference { UserId = user.Id }));
        await Task.WhenAll(
            host.InvokeAsync<Appearance>(new UpdateAppearance(new("islands", "blue", "dark"))),
            host.InvokeAsync<Appearance>(new UpdateAppearance(new("connected", "green", "light"))));
        using var session = host.OpenSession();
        var history = (await session.QueryOver<UserAppearancePreference>().Where(p => p.UserId == user.Id).OrderBy(p => p.ActiveFrom).Asc.ListAsync()).ToArray();
        Assert.Equal(3, history.Length);
        Assert.Equal(Appearance.Default, history[0].Appearance);
        Assert.Single(history, p => p.ActiveTo is null);
        Assert.All(history, p => Assert.Equal(DateTimeKind.Utc, p.ActiveFrom.Kind));
        Assert.Equal(history[0].ActiveTo, history[1].ActiveFrom);
        Assert.Equal(history[1].ActiveTo, history[2].ActiveFrom);
        await host.InvokeAsync<Appearance>(new UpdateAppearance(history[2].Appearance));
        Assert.Equal(3, await session.QueryOver<UserAppearancePreference>().Where(p => p.UserId == user.Id).RowCountAsync());
    }

    [PostgreSqlFact]
    public async Task Unsupported_appearance_is_rejected_before_anything_is_written()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_appearance_invalid_");
        var user = await host.SeedUserAsync("admin");
        host.Actor.Current = user;
        var error = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<Appearance>(new UpdateAppearance(new("floating", "blue", "system"))));
        Assert.Equal(400, error.Status);
        Assert.Equal("Nepodporované nastavení vzhledu.", error.Message);
        using var session = host.OpenSession();
        Assert.Equal(0, await session.QueryOver<UserAppearancePreference>().Where(p => p.UserId == user.Id).RowCountAsync());
    }

    [PostgreSqlFact]
    public async Task Rotated_security_stamp_ends_the_session_before_anything_is_written()
    {
        var accessor = new FixedHttpContextAccessor();
        await using var host = await AtlasTestHost.CreateAsync("atlas_appearance_rotated_", services =>
        {
            services.AddSingleton<IHttpContextAccessor>(accessor);
            services.AddScoped<ICurrentActor, HttpCurrentActor>();
        });
        var user = await host.SeedUserAsync("admin");
        accessor.SignIn(user.Id, user.SecurityStamp);
        await host.ExecuteAsync("UPDATE \"UserAccount\" SET \"SecurityStamp\"='rotated' WHERE \"Id\"=:id", user.Id);
        var error = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<Appearance>(new UpdateAppearance(new("islands", "blue", "dark"))));
        Assert.Equal(401, error.Status);
        Assert.Equal("Přihlášení vypršelo.", error.Message);
        using var session = host.OpenSession();
        Assert.Equal(0, await session.QueryOver<UserAppearancePreference>().RowCountAsync());
    }

    [PostgreSqlFact]
    public async Task An_actor_without_an_account_row_cannot_take_the_lock()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_appearance_no_account_");
        var ghost = new UserAccount { Id = Guid.NewGuid(), Username = "ghost", DisplayName = "Ghost" };
        host.Actor.Current = ghost;
        var error = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<Appearance>(new UpdateAppearance(new("islands", "blue", "dark"))));
        Assert.Equal(401, error.Status);
        Assert.Equal("Přihlaste se.", error.Message);
        using var session = host.OpenSession();
        Assert.Equal(0, await session.QueryOver<UserAppearancePreference>().Where(p => p.UserId == ghost.Id).RowCountAsync());
    }
}
