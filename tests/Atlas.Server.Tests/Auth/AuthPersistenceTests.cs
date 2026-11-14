using NHibernate;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Auth.Persistence;
using Atlas.Server.Features.Auth.OpenIdConnect.Domain;
using Atlas.Server.Features.Auth.Queries;
using Atlas.Server.Features.Auth.Recovery.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Auth;

public sealed class AuthPersistenceTests
{
    [PostgreSqlFact]
    public async Task Sign_in_facts_round_trip_as_UTC_and_order_by_newest_first()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_auth_persistence_");
        var user = await host.SeedUserAsync("admin");
        var older = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);
        await host.SeedAsync(async session =>
        {
            await session.SaveAsync(new UserSignIn { Id = Guid.NewGuid(), UserId = user.Id, AuthenticatedAt = older, AuthenticationMethod = "builtin" });
            await session.SaveAsync(new UserSignIn { Id = Guid.NewGuid(), UserId = user.Id, AuthenticatedAt = newer, AuthenticationMethod = "oidc:test" });
        });
        using var read = host.OpenSession();
        var facts = await read.QueryOver<UserSignIn>()
            .Where(x => x.UserId == user.Id)
            .OrderBy(x => x.AuthenticatedAt).Desc
            .ThenBy(x => x.Id).Desc
            .ListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal("oidc:test", facts[0].AuthenticationMethod);
        Assert.Equal(newer, facts[0].AuthenticatedAt);
        Assert.All(facts, fact => Assert.Equal(DateTimeKind.Utc, fact.AuthenticatedAt.Kind));
    }

    [PostgreSqlFact]
    public async Task Database_clock_is_UTC_and_close_to_the_application_clock()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_auth_clock_") /* own database: creates the Workspace row (Id 1, one for the installation) that other code reads */;
        await host.SeedAsync(session => session.SaveAsync(new Workspace { Id = 1, Name = "Clock", CreatedAt = DateTime.UtcNow }));
        using var session = host.OpenSession();
        using var transaction = session.BeginTransaction();
        var now = await DatabaseClock.NowAsync(session, default);
        await transaction.CommitAsync();
        Assert.True(Math.Abs((now - DateTime.UtcNow).TotalMinutes) < 1, $"database clock {now:O} is far from {DateTime.UtcNow:O}");
    }

    [PostgreSqlFact]
    public async Task Recovery_token_round_trips_its_hash_and_only_one_row_can_stay_unconsumed()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_recovery_token_") /* own database: ServerRecoveryToken allows one unconsumed row for the whole installation (UX_ServerRecoveryToken_Current) */;
        var user = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var first = System.Security.Cryptography.SHA256.HashData("first"u8.ToArray());
        var second = System.Security.Cryptography.SHA256.HashData("second"u8.ToArray());
        var issued = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        await host.SeedAsync(session => session.SaveAsync(new ServerRecoveryToken { Id = Guid.NewGuid(), UserId = user.Id, TokenHash = first, IssuedAt = issued, ExpiresAt = issued.AddDays(7) }));
        using (var read = host.OpenSession())
        {
            var stored = Assert.Single(await read.QueryOver<ServerRecoveryToken>().ListAsync());
            Assert.Equal(first, stored.TokenHash);
            Assert.Equal(DateTimeKind.Utc, stored.IssuedAt.Kind);
            Assert.Null(stored.ConsumedAt);
        }
        var conflict = await Assert.ThrowsAnyAsync<Exception>(() => host.SeedAsync(session
            => session.SaveAsync(new ServerRecoveryToken { Id = Guid.NewGuid(), UserId = user.Id, TokenHash = second, IssuedAt = issued, ExpiresAt = issued.AddDays(7) })));
        Assert.Contains("UX_ServerRecoveryToken_Current", conflict.ToString(), StringComparison.Ordinal);
    }

    [PostgreSqlFact]
    public async Task Workspace_state_reports_the_installation_and_its_name()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_workspace_state_") /* own database: needs an installation without a Workspace row (empty installation) */;
        var empty = await host.InvokeAsync<WorkspaceState>(new GetWorkspaceState());
        Assert.False(empty.Configured);
        Assert.Null(empty.Name);
        await host.SeedAsync(session => session.SaveAsync(new Workspace { Id = 1, Name = "Acme Wiki", CreatedAt = DateTime.UtcNow }));
        var configured = await host.InvokeAsync<WorkspaceState>(new GetWorkspaceState());
        Assert.True(configured.Configured);
        Assert.Equal("Acme Wiki", configured.Name);
    }

    [PostgreSqlFact]
    public async Task Database_clock_refuses_to_answer_before_the_installation_exists()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_auth_clock_empty_") /* own database: needs an installation without a Workspace row (empty installation) */;
        using var session = host.OpenSession();
        using var transaction = session.BeginTransaction();
        var error = await Assert.ThrowsAsync<ApiException>(() => DatabaseClock.NowAsync(session, default));
        await transaction.CommitAsync();
        Assert.Equal(503, error.Status);
        Assert.Equal("Wiki ještě nebyla nastavena.", error.Message);
    }

    [PostgreSqlFact]
    public async Task State_token_version_increments_and_temporal_external_identity_round_trips()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_oidc_persistence_");
        var user = await host.SeedUserAsync("admin");
        var token = new OidcStateToken { Status = "valid", Type = "state_token", CreationDate = DateTime.UtcNow, ExpirationDate = DateTime.UtcNow.AddMinutes(10) };
        await host.SeedAsync(async session =>
        {
            await session.SaveAsync(token);
            await session.SaveAsync(new ExternalIdentity { Id = Guid.NewGuid(), UserId = user.Id, Issuer = "https://idp.test", Subject = "remote-sub", ActiveFrom = DateTime.UtcNow });
        });
        await host.SeedAsync(async session =>
        {
            var stored = await session.GetAsync<OidcStateToken>(token.Id);
            stored.Status = "redeemed";
        });
        using var read = host.OpenSession();
        var updated = await read.GetAsync<OidcStateToken>(token.Id);
        Assert.Equal("redeemed", updated.Status);
        Assert.True(updated.Version > token.Version);
        var identity = Assert.Single(await read.QueryOver<ExternalIdentity>().Where(x => x.UserId == user.Id && x.ActiveTo == null).ListAsync());
        Assert.Equal("remote-sub", identity.Subject);
        Assert.Equal(DateTimeKind.Utc, identity.ActiveFrom.Kind);
    }
}
