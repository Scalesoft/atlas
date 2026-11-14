using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.YouTrack;
using Atlas.Server.Features.YouTrack.Commands;
using Atlas.Server.Features.YouTrack.Contracts;
using Atlas.Server.Features.YouTrack.Domain;
using Atlas.Server.Features.YouTrack.Queries;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Atlas.YouTrack;
using Xunit;

namespace Atlas.Server.Tests.YouTrack;

public sealed class YouTrackHandlerTests
{
    private const string SyntheticToken = "youtrack-handler-fixture-credential";
    private const string ConfiguredToken = "youtrack-configuration-fixture-credential";

    [PostgreSqlFact]
    public async Task Settings_are_admin_only_temporal_and_never_return_the_stored_token()
    {
        await using var f = await YouTrackFixture.CreateAsync(savesSettings: true);
        var initial = await f.As<YouTrackSettingsDto>(f.Admin, new GetYouTrackSettings());
        Assert.True(initial.ModuleEnabled);
        Assert.False(initial.Enabled);
        Assert.False(initial.HasToken);
        Assert.Null(initial.Version);
        Assert.Equal("disabled", initial.Status);
        Assert.Equal(f.SpaceId, Assert.Single(initial.Spaces).Id);
        await Denied(403, () => f.As<YouTrackSettingsDto>(f.Reader, new GetYouTrackSettings()));
        var request = new SaveYouTrackSettingsRequest(true, "https://tracker.example", SyntheticToken, false, [new(f.SpaceId, ["0-7"])], null);
        var saved = await f.As<YouTrackSettingsDto>(f.Admin, new SaveYouTrackSettings(request));
        Assert.True(saved.HasToken);
        Assert.Equal("configured", saved.Status);
        Assert.NotNull(saved.Version);
        Assert.Equal(new[] { "0-7" }, Assert.Single(saved.SpaceProjects).ProjectIds);
        await Denied(409, () => f.As<YouTrackSettingsDto>(f.Admin, new SaveYouTrackSettings(request)));
        await Denied(403, () => f.As<YouTrackSettingsDto>(f.Reader, new SaveYouTrackSettings(request with { ExpectedVersion = saved.Version })));
        var second = await f.As<YouTrackSettingsDto>(f.Admin, new SaveYouTrackSettings(
            request with { Token = null, ExpectedVersion = saved.Version, SpaceProjects = [new(f.SpaceId, ["0-8"])] }));
        Assert.True(second.HasToken);
        using var session = f.Host.OpenSession();
        var rows = await session.QueryOver<YouTrackConfiguration>().OrderBy(x => x.ActiveFrom).Asc.ListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(rows[0].ActiveTo, rows[1].ActiveFrom);
        Assert.Null(rows[1].ActiveTo);
        Assert.All(rows, row => Assert.DoesNotContain(SyntheticToken, row.ProtectedToken, StringComparison.Ordinal));
    }

    [PostgreSqlFact]
    public async Task Settings_reject_unsafe_urls_conflicting_token_intents_and_unknown_spaces()
    {
        await using var f = await YouTrackFixture.CreateAsync();
        var valid = new SaveYouTrackSettingsRequest(false, "https://tracker.example", null, false, [], null);
        await Denied(400, () => f.As<YouTrackSettingsDto>(f.Admin, new SaveYouTrackSettings(valid with { BaseUrl = "http://tracker.example" })));
        await Denied(400, () => f.As<YouTrackSettingsDto>(f.Admin, new SaveYouTrackSettings(valid with { Token = "x", ClearToken = true })));
        await Denied(400, () => f.As<YouTrackSettingsDto>(f.Admin, new SaveYouTrackSettings(valid with { SpaceProjects = [new(f.SpaceId, ["ok"]), new(f.SpaceId, ["ok"])] })));
        await Denied(400, () => f.As<YouTrackSettingsDto>(f.Admin, new SaveYouTrackSettings(valid with { SpaceProjects = [new(f.SpaceId, ["not valid!"])] })));
        await Denied(400, () => f.As<YouTrackSettingsDto>(f.Admin, new SaveYouTrackSettings(valid with { SpaceProjects = [new(Guid.NewGuid(), ["0-7"])] })));
        await Denied(400, () => f.As<YouTrackSettingsDto>(f.Admin, new SaveYouTrackSettings(valid with { Enabled = true })));
    }

    [PostgreSqlFact]
    public async Task Save_authorizes_the_actor_before_validating_a_malformed_body()
    {
        await using var f = await YouTrackFixture.CreateAsync();
        // Stale stamp, resolved through the same in-memory actor the fixture keeps: RequireAsync must see this
        // before the handler ever looks at the (malformed) body — legacy resolved the actor as the endpoint
        // argument, ahead of SaveAsync's own validation.
        await f.Host.ExecuteAsync("UPDATE \"UserAccount\" SET \"SecurityStamp\"='rotated' WHERE \"Id\"=:id", f.Admin.Id);
        var malformed = new SaveYouTrackSettingsRequest(false, "http://tracker.example", null, false, [], null);
        await Denied(401, () => f.As<YouTrackSettingsDto>(f.Admin, new SaveYouTrackSettings(malformed)));
    }

    [PostgreSqlFact]
    public async Task Actor_rereads_the_account_even_when_the_request_actor_came_from_the_same_session()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_youtrack_actor_", services =>
        {
            services.AddScoped<YouTrackActor>();
            // AsyncLocal: each test sharing the host signs in its own request.
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            // The production actor resolver, so the request actor and the row YouTrackActor re-reads are the
            // same instance until RefreshAsync overwrites it in place.
            services.AddScoped<ICurrentActor, HttpCurrentActor>();
        }, sharedAs: "youtrack-actor");
        var user = await host.SeedUserAsync("youtrack-actor");
        var request = new FixedHttpContextAccessor();
        request.SignIn(user.Id, user.SecurityStamp);
        host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = request.HttpContext;
        var error = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<Guid>(new ProbeStaleYouTrackActor(user.Id)));
        Assert.Equal(401, error.Status);
        Assert.Equal("Přihlášení vypršelo.", error.Message);
    }

    [PostgreSqlFact]
    public async Task Configuration_read_by_a_reader_keeps_the_token_protected()
    {
        // Why: ReadYouTrackConfiguration needs only Reader on a space and its answer leaves the transaction, so any Reader who can
        // send the message must not get the plain upstream token, as a property, in serialized form or in a log line. The runner
        // unprotects the protected copy with the integration's own data-protection purpose.
        await using var f = await YouTrackFixture.CreateAsync(savesSettings: true);
        var request = new SaveYouTrackSettingsRequest(true, "https://tracker.example", SyntheticToken, false, [new(f.SpaceId, ["0-7"])], null);
        await f.As<YouTrackSettingsDto>(f.Admin, new SaveYouTrackSettings(request));
        var read = await f.As<YouTrackProtectedConfiguration>(f.Reader, new ReadYouTrackConfiguration(f.SpaceId));
        Assert.NotEmpty(read.ProtectedToken);
        Assert.DoesNotContain(SyntheticToken, JsonSerializer.Serialize(read), StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticToken, read.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(read.ProtectedToken, read.ToString(), StringComparison.Ordinal);
        var protector = f.Host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(SaveYouTrackSettingsHandler.TokenPurpose);
        Assert.Equal(SyntheticToken, read.Unprotect(protector).Options.Token);
    }

    [PostgreSqlFact]
    public async Task Configuration_only_token_is_protected_in_the_read_and_does_not_change_the_revision()
    {
        // Why: a token that comes only from the server configuration (no stored row) is protected for the message result and
        // unprotected by the runner. Every other test configures an empty token, so this path never ran with a real value. The
        // revision must stay the same between reads (a different one is a 409 after every upstream call) and must not depend on
        // the protected text, which differs on each protection.
        await using var f = await YouTrackFixture.CreateAsync(ConfiguredToken);
        var first = await f.As<YouTrackProtectedConfiguration>(f.Reader, new ReadYouTrackConfiguration(f.SpaceId));
        var second = await f.As<YouTrackProtectedConfiguration>(f.Reader, new ReadYouTrackConfiguration(f.SpaceId));
        Assert.Equal(first.Revision, second.Revision);
        Assert.NotEqual(first.ProtectedToken, second.ProtectedToken);
        Assert.NotEmpty(first.ProtectedToken);
        Assert.NotEqual(ConfiguredToken, first.ProtectedToken);
        Assert.DoesNotContain(ConfiguredToken, JsonSerializer.Serialize(first), StringComparison.Ordinal);
        var protector = f.Host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(SaveYouTrackSettingsHandler.TokenPurpose);
        Assert.Equal(ConfiguredToken, first.Unprotect(protector).Options.Token);
        await f.Host.InvokeAsync(new EnsureYouTrackUnchanged(f.SpaceId, first.Revision));
    }

    [PostgreSqlFact]
    public async Task Recheck_after_the_upstream_call_refuses_a_deactivated_account_and_a_demoted_administrator()
    {
        // Why: EnsureYouTrackUnchanged is the only authorization between the upstream answer and its release. The HTTP test of a
        // revoked reader pins its 404 (lost space role); the other two refusals are separate branches: 401 for an account
        // deactivated during the call, 403 for an administrator demoted during "Test spojení", the route that returns the
        // administrator-only project list.
        await using var f = await YouTrackFixture.CreateAsync();
        var space = await f.As<YouTrackProtectedConfiguration>(f.Reader, new ReadYouTrackConfiguration(f.SpaceId));
        var connection = await f.As<YouTrackProtectedConfiguration>(f.Admin, new ReadYouTrackConfiguration(null));
        await f.Host.ExecuteAsync("UPDATE \"UserAccount\" SET \"IsActive\"=false WHERE \"Id\"=:id", f.Reader.Id);
        await f.Host.ExecuteAsync("UPDATE \"UserAccount\" SET \"IsWikiAdmin\"=false WHERE \"Id\"=:id", f.Admin.Id);
        f.Host.Actor.Current = f.Reader;
        await Denied(401, () => f.Host.InvokeAsync(new EnsureYouTrackUnchanged(f.SpaceId, space.Revision)));
        f.Host.Actor.Current = f.Admin;
        await Denied(403, () => f.Host.InvokeAsync(new EnsureYouTrackUnchanged(null, connection.Revision)));
    }

    [PostgreSqlFact]
    public async Task After_migration_2026110196_an_existing_configuration_gets_its_project_keys_on_the_next_youtrack_read()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_youtrack_keys_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110196);
        const string Admin = "00000000-0000-0000-0000-000000000001", Space = "00000000-0000-0000-0000-00000000000a";
        var row = Guid.NewGuid();
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES ('{Admin}','admin','Admin',true,true,'s1');
            INSERT INTO "YouTrackConfiguration"("Id","Enabled","BaseUrl","ProtectedToken","SpaceProjectsJson","ChangedById","ActiveFrom")
              VALUES ('{row}',true,'https://tracker.example','','{"{"}"{Space}":["0-7","0-8"]{"}"}','{Admin}',now());
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, services =>
        {
            services.AddDataProtection().SetApplicationName("ScaleAtlas");
            services.AddAtlasYouTrack(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["YouTrack:Enabled"] = "true" }).Build());
            services.AddYouTrack();
        });
        async Task<string[]> Keys()
        {
            using var scope = host.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<YouTrackConfigurationReader>().ProjectKeysAsync(Guid.Parse(Space), CancellationToken.None);
        }
        Assert.Equal("{}", await OutboxTests.ScalarAsync(host.ConnectionString, """SELECT "ProjectKeysJson" FROM "YouTrackConfiguration" """));
        Assert.Empty(await Keys());
        // The next read of the space's projects: 0-8 is gone, an invalid name is not stored, a stale revision changes nothing.
        await host.InvokeAsync(new StoreYouTrackProjectKeys(Guid.NewGuid().ToString("N"), new Dictionary<string, string?> { ["0-7"] = "OLD" }));
        Assert.Empty(await Keys());
        await host.InvokeAsync(new StoreYouTrackProjectKeys(row.ToString("N"), new Dictionary<string, string?> { ["0-7"] = "PX2", ["0-8"] = null, ["0-9"] = "not valid" }));
        Assert.Equal(["PX2"], await Keys());
        Assert.Equal("""{"0-7":"PX2"}""", await OutboxTests.ScalarAsync(host.ConnectionString, """SELECT "ProjectKeysJson" FROM "YouTrackConfiguration" """));
    }

    private static async Task Denied(int status, Func<Task> action)
        => Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);
}
