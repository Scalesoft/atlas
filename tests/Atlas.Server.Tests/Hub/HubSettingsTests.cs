using Atlas.Server.Features.Administration;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Hub.Commands;
using Atlas.Server.Features.Hub.Contracts;
using Atlas.Server.Features.Hub.Directory;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Queries;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Atlas.Server.Features.Hub.Queries;

namespace Atlas.Server.Tests.Hub;

public sealed class HubSettingsTests
{
    [PostgreSqlFact]
    public async Task Revised_configuration_requires_new_identity_proof_in_profile_and_admin_diagnostics()
    {
        await using var host = await CreateAsync();
        var (member, _, _) = await HubEffectiveAuthorityTests.SeedAsync(host, false);
        host.Actor.Current = member;
        var previous = (await host.InvokeAsync<HubProfileView>(new GetHubProfile())).Identity!;
        Assert.Equal("Active", previous.State);
        await host.SeedAsync(s => s.CreateSQLQuery("""
            UPDATE "HubConnectionRevision" SET "ActiveTo"=clock_timestamp() WHERE "ActiveTo" IS NULL;
            INSERT INTO "HubConnectionRevision"("Id","ConnectionId","Issuer","BaseUrl","ProtectedToken","Mode","ChangedById","ActiveFrom")
            SELECT gen_random_uuid(),"ConnectionId","Issuer","BaseUrl","ProtectedToken","Mode","ChangedById","ActiveTo"
            FROM "HubConnectionRevision" ORDER BY "ActiveFrom" DESC LIMIT 1
            """).ExecuteUpdateAsync());
        var profile = (await host.InvokeAsync<HubProfileView>(new GetHubProfile())).Identity!;
        Assert.Equal("Unverified", profile.State);
        Assert.Null(profile.VerifiedUntil);
        Assert.Equal(previous.LastSuccessAt, profile.LastSuccessAt);
        Assert.Equal(previous.LastAttemptAt, profile.LastAttemptAt);
        host.Actor.Current = await host.SeedUserAsync("recovery", true);
        var settings = await host.InvokeAsync<HubSettingsView>(new GetHubSettings());
        var state = Assert.Single(settings.Identities);
        Assert.Equal("Unverified", state.State);
        Assert.Null(state.VerifiedUntil);
        Assert.Equal(previous.LastSuccessAt, state.LastSuccessAt);
        Assert.Equal("Unverified", settings.Health!.State);
    }

    [PostgreSqlFact]
    public async Task Configuration_rejects_an_issuer_with_ambiguous_existing_account_links()
    {
        await using var host = await CreateAsync();
        host.Actor.Current = await host.SeedUserAsync("admin", true);
        await host.SeedAsync(async session =>
        {
            foreach (var subject in new[] { "first-subject", "second-subject" })
                await session.SaveAsync(new Atlas.Server.Features.Auth.OpenIdConnect.Domain.ExternalIdentity
                {
                    Id = Guid.NewGuid(), UserId = host.Actor.Current.Id, Issuer = "https://hub.example.test/hub", Subject = subject, ActiveFrom = DateTime.UtcNow
                });
        });
        var prepared = await host.InvokeAsync<PreparedHubSettings>(new PrepareHubSettings(new("https://hub.example.test/hub", "https://hub.example.test/hub", "Preview", "synthetic-secret", null)));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<HubSettingsView>(new CommitHubSettings(prepared)))).Status);
        using var read = host.OpenSession();
        Assert.Empty(await read.QueryOver<HubConnectionRevision>().ListAsync());
    }

    [PostgreSqlFact]
    public async Task Configuration_encrypts_token_and_rejects_stale_revision_and_credential_destination_changes()
    {
        await using var host = await CreateAsync();
        host.Actor.Current = await host.SeedUserAsync("admin", true);
        var request = new SaveHubSettings("https://hub.example.test/hub", "https://hub.example.test/hub", "Preview", "synthetic-secret", null);
        Assert.DoesNotContain("synthetic-secret", request.ToString(), StringComparison.Ordinal);
        var prepared = await host.InvokeAsync<PreparedHubSettings>(new PrepareHubSettings(request));
        Assert.DoesNotContain("synthetic-secret", prepared.ProtectedToken, StringComparison.Ordinal);
        var view = await host.InvokeAsync<HubSettingsView>(new CommitHubSettings(prepared));
        Assert.True(view.HasToken);
        Assert.Equal("Preview", view.Mode);
        Assert.DoesNotContain("synthetic-secret", System.Text.Json.JsonSerializer.Serialize(view), StringComparison.Ordinal);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<HubSettingsView>(new CommitHubSettings(prepared)))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<PreparedHubSettings>(new PrepareHubSettings(request with
        {
            BaseUrl = "https://different.example.test/hub", Token = null, ExpectedVersion = view.Version
        })))).Status);
        var retained = await host.InvokeAsync<PreparedHubSettings>(new PrepareHubSettings(request with { Token = null, ExpectedVersion = view.Version }));
        Assert.Equal(prepared.ProtectedToken, retained.ProtectedToken);
    }

    [PostgreSqlFact]
    public async Task Enforced_requires_fresh_auth_and_a_local_recovery_admin()
    {
        var guard = new TrackingGuard();
        await using var host = await CreateAsync(guard);
        host.Actor.Current = await host.SeedUserAsync("admin", true);
        var prepared = await host.InvokeAsync<PreparedHubSettings>(new PrepareHubSettings(new("https://hub.example.test/hub", "https://hub.example.test/hub", "Enforced", "synthetic-secret", null)));
        Assert.Equal(428, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<HubSettingsView>(new CommitHubSettings(prepared)))).Status);
        Assert.Equal("hub.connection", guard.LastKind);
        guard.Allow = true;
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<HubSettingsView>(new CommitHubSettings(prepared)))).Status);
    }

    [PostgreSqlFact]
    public async Task Binding_uses_verified_opaque_id_has_managed_projection_and_rejects_stale_or_unconfirmed_global_edit()
    {
        var guard = new TrackingGuard();
        await using var host = await CreateAsync(guard);
        host.Actor.Current = await host.SeedUserAsync("admin", true);
        var prepared = await host.InvokeAsync<PreparedHubSettings>(new PrepareHubSettings(new("https://hub.example.test/hub", "https://hub.example.test/hub", "Preview", "synthetic-secret", null)));
        var settings = await host.InvokeAsync<HubSettingsView>(new CommitHubSettings(prepared));
        using var read = host.OpenSession();
        var revision = await read.QueryOver<HubConnectionRevision>().Where(x => x.ActiveTo == null).SingleOrDefaultAsync();
        var attached = await host.InvokeAsync<HubSettingsView>(new CommitHubGroup(revision.Id, settings.Version, new HubDirectoryGroup("opaque-external-id", "Developers")));
        var binding = Assert.Single(attached.Bindings);
        Assert.Equal("opaque-external-id", binding.ExternalGroupId);
        Assert.False(binding.GrantsWikiAdmin);
        var group = Assert.Single(await host.InvokeAsync<IReadOnlyList<GroupSummary>>(new ListGroups()));
        Assert.Equal("hub", group.Source);
        Assert.Equal(428, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<HubSettingsView>(new ChangeHubBinding(binding.GroupId, new(true, true, binding.Version))))).Status);
        var disabled = await host.InvokeAsync<HubSettingsView>(new ChangeHubBinding(binding.GroupId, new(false, false, binding.Version)));
        Assert.False(Assert.Single(disabled.Bindings).Enabled);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<HubSettingsView>(new ChangeHubBinding(binding.GroupId, new(true, false, binding.Version))))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<HubSettingsView>(new CommitHubGroup(revision.Id, settings.Version, new HubDirectoryGroup("opaque-external-id", "Different name"))))).Status);
    }

    private static Task<AtlasTestHost> CreateAsync(ISecurityChangeGuard? guard = null) => AtlasTestHost.CreateAsync("atlas_hub_settings_", services =>
    {
        services.AddAdministration();
        services.AddDataProtection();
        services.AddSingleton(new AtlasOidcOptions { Providers = [new AtlasOidcProvider { Id = "hub", DisplayName = "Hub", Issuer = "https://hub.example.test/hub", ClientId = "atlas" }] });
        if (guard is not null) services.AddSingleton(guard);
    });

    private sealed class TrackingGuard : ISecurityChangeGuard
    {
        public bool Allow { get; set; }
        public string? LastKind { get; private set; }
        public Task RequireAsync(UserAccount actor, SecurityChange change, CancellationToken ct)
        {
            LastKind = change.Kind;
            if (!Allow) throw new ApiException(428, "Ověřte identitu.");
            return Task.CompletedTask;
        }
    }
}
