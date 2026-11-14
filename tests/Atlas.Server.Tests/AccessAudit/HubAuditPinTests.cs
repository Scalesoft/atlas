using Atlas.Server.Features.Administration;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Hub.Commands;
using Atlas.Server.Features.Hub.Contracts;
using Atlas.Server.Features.Hub.Directory;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.AccessAudit;

/// <summary>
/// The audit (HubChange and its AccessAuditEvent) and the binding history of the Hub's configuration, bindings and observed identities,
/// pinned while the database triggers of the Hub migrations wrote them. C# writes them since 3.6.
/// </summary>
public sealed class HubAuditPinTests
{
    [PostgreSqlFact]
    public async Task Connection_binding_and_identity_changes_audit_as_the_triggers_did()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_hub_pin_", services =>
        {
            services.AddAdministration();
            services.AddDataProtection();
            services.AddSingleton(new AtlasOidcOptions { Providers = [new AtlasOidcProvider { Id = "hub", DisplayName = "Hub", Issuer = "https://hub.example.test/hub", ClientId = "atlas" }] });
        });
        var admin = await host.SeedUserAsync("admin", true);
        var member = await host.SeedUserAsync("member");
        var identity = Guid.NewGuid();
        // Enforced needs a local recovery admin with a password.
        await host.SeedAsync(session => session.CreateSQLQuery("""
            INSERT INTO "ExternalIdentity"("Id","UserId","Issuer","Subject","ActiveFrom")
            VALUES (:identity,:user,'https://hub.example.test/hub','opaque-user',statement_timestamp()-interval '1 hour');
            INSERT INTO "BuiltinCredential"("UserId","PasswordHash") VALUES (:admin,'recovery-hash')
            """).SetParameter("identity", identity).SetParameter("user", member.Id).SetParameter("admin", admin.Id).ExecuteUpdateAsync());
        var mark = await AuditPin.MarkAsync(host.ConnectionString);

        host.Actor.Current = admin;
        var request = new SaveHubSettings("https://hub.example.test/hub", "https://hub.example.test/hub", "Preview", "synthetic-secret", null);
        var settings = await host.InvokeAsync<HubSettingsView>(new CommitHubSettings(await host.InvokeAsync<PreparedHubSettings>(new PrepareHubSettings(request))));
        settings = await host.InvokeAsync<HubSettingsView>(new CommitHubSettings(await host.InvokeAsync<PreparedHubSettings>(
            new PrepareHubSettings(request with { Mode = "Enforced", Token = null, ExpectedVersion = settings.Version }))));
        Guid revision;
        using (var read = host.OpenSession())
        {
            revision = (await read.QueryOver<HubConnectionRevision>().Where(x => x.ActiveTo == null).SingleOrDefaultAsync()).Id;
        }
        var attached = await host.InvokeAsync<HubSettingsView>(new CommitHubGroup(revision, settings.Version, new HubDirectoryGroup("opaque-group", "Developers")));
        var binding = Assert.Single(attached.Bindings);
        await host.InvokeAsync<HubSettingsView>(new ChangeHubBinding(binding.GroupId, new(true, true, binding.Version)));
        var attempt = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        var observed = new HubUserObservation(attempt.ExternalUserId, HubDirectoryAccountState.Active, [new("opaque-group", "Developers")]);
        await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(attempt, observed));
        var ban = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(ban, observed with { State = HubDirectoryAccountState.Disabled, Groups = [] }));

        // The synchronization has no actor.
        Assert.Equal(
        [
            "hub.connection.changed admin HubChangeId",
            "hub.connection.changed admin HubChangeId",
            "group.created admin GroupRevisionId",
            "hub.binding.changed admin HubChangeId",
            "hub.binding.changed admin HubChangeId",
            "membership.added - GroupMemberId",
            "hub.identity.changed - HubChangeId",
            "hub.identity.changed - HubChangeId",
            "membership.removed - GroupMemberId",
        ], await AuditPin.EventsAsync(host.ConnectionString, mark));
        Assert.Equal(["admin closed true false", "admin open true true"], await AuditPin.RevisionsAsync(host.ConnectionString, "HubGroupBindingRevision", "ChangedBy", "Enabled", "GrantsWikiAdmin"));
        Assert.Equal(
        [
            "connection.created t f f f f - -",
            "connection.updated t f f f f - -",
            "binding.attached f t t f f - -",
            "binding.updated f t t f f - -",
            "identity.observed f f f t t opaque-user 0 1",
            "identity.observed f f f t t opaque-user 1 2",
        ], await AuditPin.LinesAsync(host.ConnectionString, """
            SELECT concat_ws(' ', "Reason", "ConnectionRevisionId" IS NOT NULL, "BindingRevisionId" IS NOT NULL, "GroupId" IS NOT NULL,
                "UserId" IS NOT NULL, "ExternalIdentityId" IS NOT NULL, "ExternalUserId", COALESCE("PreviousState"::text,'-'), COALESCE("CurrentState"::text,'-')), "Id"
            FROM "HubChange"
            """));
    }

    /// <summary>Preview observes and closes nothing: a membership the observation no longer backs stays open, so neither
    /// membership.removed nor a share revocation is written; only the observed identity is.</summary>
    [PostgreSqlFact]
    public async Task Preview_sync_closes_no_membership_and_audits_none()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_hub_preview_pin_", services =>
        {
            services.AddAdministration();
            services.AddDataProtection();
            services.AddSingleton(new AtlasOidcOptions { Providers = [new AtlasOidcProvider { Id = "hub", DisplayName = "Hub", Issuer = "https://hub.example.test/hub", ClientId = "atlas" }] });
        });
        var admin = await host.SeedUserAsync("admin", true);
        var member = await host.SeedUserAsync("member");
        var identity = Guid.NewGuid();
        await host.SeedAsync(session => session.CreateSQLQuery("""
            INSERT INTO "ExternalIdentity"("Id","UserId","Issuer","Subject","ActiveFrom")
            VALUES (:identity,:user,'https://hub.example.test/hub','opaque-user',statement_timestamp()-interval '1 hour')
            """).SetParameter("identity", identity).SetParameter("user", member.Id).ExecuteUpdateAsync());
        host.Actor.Current = admin;
        var request = new SaveHubSettings("https://hub.example.test/hub", "https://hub.example.test/hub", "Preview", "synthetic-secret", null);
        var settings = await host.InvokeAsync<HubSettingsView>(new CommitHubSettings(await host.InvokeAsync<PreparedHubSettings>(new PrepareHubSettings(request))));
        Guid revision;
        using (var read = host.OpenSession())
        {
            revision = (await read.QueryOver<HubConnectionRevision>().Where(x => x.ActiveTo == null).SingleOrDefaultAsync()).Id;
        }
        var binding = Assert.Single((await host.InvokeAsync<HubSettingsView>(new CommitHubGroup(revision, settings.Version, new HubDirectoryGroup("opaque-group", "Developers")))).Bindings);
        var membership = Guid.NewGuid();
        await host.SeedAsync(session => session.CreateSQLQuery("""
            INSERT INTO "GroupMember"("Id","GroupId","UserId","ActiveFrom") VALUES (:id,:group,:user,statement_timestamp()-interval '1 hour')
            """).SetParameter("id", membership).SetParameter("group", binding.GroupId).SetParameter("user", member.Id).ExecuteUpdateAsync());
        var mark = await AuditPin.MarkAsync(host.ConnectionString);

        var attempt = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        var result = await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(attempt,
            new HubUserObservation(attempt.ExternalUserId, HubDirectoryAccountState.Disabled, [])));

        Assert.Equal("preview", result.Status);
        Assert.Equal(["hub.identity.changed - HubChangeId"], await AuditPin.EventsAsync(host.ConnectionString, mark));
        Assert.Empty(await AuditPin.ShareEventsAsync(host.ConnectionString));
        using var check = host.OpenSession();
        Assert.Null((await check.GetAsync<Atlas.Server.Features.Administration.Domain.GroupMember>(membership))!.ActiveTo);
    }
}
