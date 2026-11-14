using Atlas.Server.Features.Auth.OpenIdConnect.Commands;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageAccess.Commands;
using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.Attachments.Queries;
using Atlas.Server.Features.ExternalSharing;
using Atlas.Server.Features.Administration;
using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Tests.Vaults;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Hub;

public sealed class HubEffectiveAuthorityTests
{
    [PostgreSqlFact]
    public async Task Access_cards_identify_the_external_group_source_without_confusing_local_groups()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_hub_access_source_", services => services.AddAdministration());
        var (member, group, _) = await SeedAsync(host, false);
        var local = await host.SeedGroupAsync("Local group", member);
        var space = await host.SeedSpaceAsync("Sources");
        var grantEnd = DateTime.UtcNow.AddDays(1);
        var hubGrant = new SpaceGrant { Id = Guid.NewGuid(), SpaceId = space, GroupId = group, Role = 1, ExpiresAt = grantEnd };
        await host.SeedAsync(async session =>
        {
            var appearance = await session.GetAsync<Atlas.Server.Features.Spaces.Domain.Space>(space);
            appearance.Icon = "server";
            appearance.Color = "green";
            await session.SaveAsync(hubGrant);
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = space, GroupId = local, Role = 1 });
        });
        host.Actor.Current = member;
        var card = await host.InvokeAsync<Atlas.Server.Features.Access.Contracts.PrincipalAccess>(new Atlas.Server.Features.Access.Queries.GetMyAccess());
        var entry = Assert.Single(card.Entries);
        Assert.Equal("server", entry.SpaceIcon);
        Assert.Equal("green", entry.SpaceColor);
        var sources = entry.Sources;
        Assert.Equal("hub", sources.Single(x => x.GroupId == group).GroupSource);
        Assert.Equal("local", sources.Single(x => x.GroupId == local).GroupSource);
        var hub = sources.Single(x => x.GroupId == group);
        Assert.True(hub.VerificationLimited);
        Assert.True(hub.ExpiresAt < hub.GrantExpiresAt);
        Assert.Equal(grantEnd.ToString("O")[..23], hub.GrantExpiresAt!.Value.ToString("O")[..23]);
        Assert.False(sources.Single(x => x.GroupId == local).VerificationLimited);

        // A real grant ending before verification must still be shown as an expiring permission.
        await host.SeedAsync(async session =>
        {
            var grant = await session.GetAsync<SpaceGrant>(hubGrant.Id);
            var now = DateTime.UtcNow;
            grant.ActiveTo = now;
            await session.FlushAsync();
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = space, GroupId = group, Role = 1, ActiveFrom = now, ExpiresAt = now.AddSeconds(30) });
        });
        card = await host.InvokeAsync<Atlas.Server.Features.Access.Contracts.PrincipalAccess>(new Atlas.Server.Features.Access.Queries.GetMyAccess());
        Assert.False(Assert.Single(card.Entries).Sources.Single(x => x.GroupId == group).VerificationLimited);
    }

    [PostgreSqlFact]
    public async Task Access_card_never_presents_a_lapsed_Hub_verification_as_a_working_path()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_hub_access_lapsed_", services => services.AddAdministration());
        var (member, group, identity) = await SeedAsync(host, false);
        var space = await host.SeedSpaceAsync("Lapsed");
        await host.SeedAsync(async session => await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = space, GroupId = group, Role = 1 }));
        await ExpireAsync(host, identity);
        host.Actor.Current = member;
        var card = await host.InvokeAsync<Atlas.Server.Features.Access.Contracts.PrincipalAccess>(new Atlas.Server.Features.Access.Queries.GetMyAccess());
        // The grant itself is unlimited, so only the lapsed lease can say that this path gives nothing now.
        Assert.All(card.Entries, entry => Assert.Null(entry.Level));
        Assert.DoesNotContain(card.Entries.SelectMany(x => x.Sources), x => x.Effective && x.GroupId == group);
    }

    [PostgreSqlFact]
    public async Task Attachment_revalidation_returns_the_live_lease_and_rejects_an_invalid_personal_token()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_attachment_lease_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var (member, _, identity) = await SeedAsync(host, true);
        var space = await host.SeedSpaceAsync("Downloads");
        var page = new Page { Id = Guid.NewGuid(), SpaceId = space, OwnerId = member.Id };
        var attachment = new Attachment { Id = Guid.NewGuid(), PageId = page.Id, UploadedBy = member.Id, Name = "download.txt", Sha256 = new string('a', 64), Size = 1, ActiveFrom = DateTime.UtcNow };
        await host.SeedAsync(async session => { await session.SaveAsync(page); await session.SaveAsync(attachment); });
        host.Actor.Current = member;
        var content = await host.InvokeAsync<AttachmentContentDto>(new OpenAttachment(attachment.Id, false));
        Assert.NotNull(content.AuthorityUntil);
        var invalidToken = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<AttachmentContentDto>(new OpenAttachment(attachment.Id, false, Guid.NewGuid())));
        Assert.Equal(401, invalidToken.Status);
        await ExpireAsync(host, identity);
        var expired = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<AttachmentContentDto>(new OpenAttachment(attachment.Id, false)));
        Assert.Equal(404, expired.Status);
    }

    [PostgreSqlFact]
    public async Task Hub_admin_cannot_convert_a_short_lease_into_permanent_page_grants()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_page_delegation_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var (member, _, _) = await SeedAsync(host, true);
        var space = await host.SeedSpaceAsync("Delegation");
        var page = new Page { Id = Guid.NewGuid(), SpaceId = space, OwnerId = member.Id };
        var recipient = await host.SeedUserAsync("recipient");
        await host.SeedAsync(s => s.SaveAsync(page));
        host.Actor.Current = member;
        var denied = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync(new SetPageGrant(page.Id,
            new UpdatePageGrant(member.Id, null, "Admin", null))));
        Assert.Equal(403, denied.Status);
        using var session = host.OpenSession();
        var until = (await new SpaceRoles(session).AdminAuthorityAsync(member, space, CancellationToken.None)).ExpiresAt!.Value;
        await host.InvokeAsync(new SetPageGrant(page.Id, new UpdatePageGrant(recipient.Id, null, "Reader", null, null, until.AddSeconds(-1))));
        await host.SeedAsync(s => s.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), UserId = member.Id, SpaceId = space, Role = 3 }));
        await host.InvokeAsync(new SetPageGrant(page.Id, new UpdatePageGrant(member.Id, null, "Admin", null)));
    }

    [PostgreSqlFact]
    public async Task Hub_admin_sees_another_accounts_vault_card_only_through_explicit_vault_authority()
    {
        // Own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton).
        await using var fixture = await VaultFixture.CreateAsync(isolated: true);
        var (member, group, identity) = await SeedAsync(fixture.Host, true);
        var location = await fixture.LocalLocationAsync();
        var registered = await fixture.AttachAsync(location.Id, "hub.kdbx", "argon2d-aes-gzip.kdbx");
        using var scope = fixture.Host.Services.CreateScope();
        var kind = scope.ServiceProvider.GetServices<IAccessKind>().Single(k => k.Descriptor.Id == "vault");
        var principal = AccessPrincipal.Of(fixture.Admin);
        Assert.Empty(await kind.ForPrincipalAsync(principal, new AccessScope(member, null), CancellationToken.None));
        await fixture.As(fixture.SpaceAdmin, new SetVaultGrant(registered.Id, new UpdateVaultGrant(null, group, "manage", null)));
        Assert.Single(await kind.ForPrincipalAsync(principal, new AccessScope(member, null), CancellationToken.None));
        using (var session = fixture.Host.OpenSession())
        {
            var vault = await session.GetAsync<Vault>(registered.Id);
            var snapshot = await new VaultGrantSnapshots(session).ForVaultsAsync(member, [vault], CancellationToken.None);
            var decision = snapshot.AuthorizeVault(vault, VaultAccessLevel.Manage, DateTime.UtcNow);
            Assert.True(decision.Allowed);
            Assert.NotNull(decision.Until);
            Assert.False(snapshot.AuthorizeVault(vault, VaultAccessLevel.Manage, decision.Until.Value).Allowed);
        }
        await ExpireAsync(fixture.Host, identity);
        Assert.Empty(await kind.ForPrincipalAsync(principal, new AccessScope(member, null), CancellationToken.None));
    }

    [PostgreSqlFact]
    public async Task Managed_issuer_claims_do_not_create_or_remove_persisted_admin_sources()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_claims_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var (member, _, identity) = await SeedAsync(host, true);
        await ExpireAsync(host, identity);
        var resolved = await host.InvokeAsync<UserAccount>(new ResolveExternalIdentity("https://hub.example.test/hub", false,
            "opaque-user", "Hub user", null, null, true));
        Assert.False(resolved.IsWikiAdmin);
        await host.ExecuteAsync("UPDATE \"UserAccount\" SET \"IsWikiAdmin\"=true,\"OidcAdminIssuer\"='https://hub.example.test/hub' WHERE \"Id\"=:id", member.Id);
        resolved = await host.InvokeAsync<UserAccount>(new ResolveExternalIdentity("https://hub.example.test/hub", false,
            "opaque-user", "Hub user", null, null, false));
        Assert.True(resolved.IsWikiAdmin);
        Assert.Equal("https://hub.example.test/hub", resolved.OidcAdminIssuer);
    }

    [PostgreSqlFact]
    public async Task Hub_admin_is_live_document_authority_without_persisting_or_granting_vault_authority()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_authority_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var (member, group, identity) = await SeedAsync(host, true);
        var recovery = await host.SeedUserAsync("recovery", true);
        var space = await host.SeedSpaceAsync("Private");
        var page = new Page { Id = Guid.NewGuid(), SpaceId = space, OwnerId = recovery.Id };
        await host.SeedAsync(s => s.SaveAsync(page));
        host.Actor.Current = member;
        using (var scope = host.Services.CreateScope())
        {
            var scopedSession = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
            using var transaction = scopedSession.BeginTransaction();
            var vaultAccess = new VaultAccess(scopedSession, scope.ServiceProvider.GetRequiredService<IContentAccess>(),
                scope.ServiceProvider.GetRequiredService<IAtlasLocks>(), new SpaceRoles(scopedSession), new VaultGrantSnapshots(scopedSession));
            var denied = await Assert.ThrowsAsync<ApiException>(() => vaultAccess.RequireSpaceAdminAsync(space, CancellationToken.None));
            Assert.Equal(404, denied.Status);
        }
        using (var session = host.OpenSession())
        {
            var authority = await new SpaceRoles(session).AdminAuthorityAsync(member, space, CancellationToken.None);
            Assert.True(authority.Allowed);
            Assert.NotNull(authority.ExpiresAt);
            var snapshot = await new ResourceGrantSnapshots(session).ForPageAsync(member, page, CancellationToken.None);
            Assert.True(snapshot.Authorize(page, AccessOperation.EditContent, DateTimeOffset.UtcNow).Allowed);
            Assert.Equal(authority.ExpiresAt, snapshot.Authority(page, AccessOperation.ReadContent, DateTimeOffset.UtcNow).Until);
            Assert.False(snapshot.Authorize(page, AccessOperation.EditContent, new DateTimeOffset(authority.ExpiresAt!.Value)).Allowed);
            var vault = new Vault { Id = Guid.NewGuid(), SpaceId = space };
            var vaults = await new VaultGrantSnapshots(session).ForVaultsAsync(member, [vault], CancellationToken.None);
            Assert.False(vaults.AuthorizeVault(vault, VaultAccessLevel.Read, DateTime.UtcNow).Allowed);
            Assert.Equal(3, await session.CreateSQLQuery("SELECT \"Role\" FROM \"EffectivePageAccess\" WHERE \"UserId\"=:user AND \"PageId\"=:page")
                .SetParameter("user", member.Id).SetParameter("page", page.Id).UniqueResultAsync<int>());
        }
        await ExpireAsync(host, identity);
        using var check = host.OpenSession();
        Assert.False((await new SpaceRoles(check).AdminAuthorityAsync(member, space, CancellationToken.None)).Allowed);
        Assert.False((await check.GetAsync<UserAccount>(member.Id)).IsWikiAdmin);
        Assert.True((await new SpaceRoles(check).AdminAuthorityAsync(recovery, space, CancellationToken.None)).Allowed);
    }

    [PostgreSqlFact]
    public async Task Sharing_lifetime_uses_the_longest_independent_source_in_application_and_SQL()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_share_lifetime_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var (member, _, _) = await SeedAsync(host, true);
        var space = await host.SeedSpaceAsync("Sharing");
        var page = new Page { Id = Guid.NewGuid(), SpaceId = space, OwnerId = member.Id };
        await host.SeedAsync(s => s.SaveAsync(page));
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        var roles = new SpaceRoles(session);
        var content = scope.ServiceProvider.GetRequiredService<IContentAccess>();
        var locks = scope.ServiceProvider.GetRequiredService<IAtlasLocks>();
        var sharing = new ExternalSharingAccess(session, locks, new AdministrationAccess(session, content, locks, roles), content, new ResourceGrantSnapshots(session), host.Database);
        var until = (await roles.AdminAuthorityAsync(member, space, CancellationToken.None)).ExpiresAt!.Value;
        async Task AssertUntil(DateTime expiry, bool expected)
        {
            Assert.Equal(expected, await sharing.CanShareUntilAsync(member, page, expiry, CancellationToken.None));
            Assert.Equal(expected, await ShareRevocation.IssuerKeepsShareAsync(session, member.Id, space, page.Id, expiry, CancellationToken.None));
        }
        await AssertUntil(until, true);
        await AssertUntil(until.AddMinutes(1), false);
        await host.SeedAsync(s => s.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), UserId = member.Id, SpaceId = space, Role = 3 }));
        await AssertUntil(until.AddHours(1), true);
        var snapshot = await new ResourceGrantSnapshots(session).ForPageAsync(member, page, CancellationToken.None);
        Assert.Null(snapshot.Authority(page, AccessOperation.ReadContent, DateTimeOffset.UtcNow).Until);
        var privateDraft = new Page { Id = page.Id, SpaceId = space, OwnerId = Guid.NewGuid() };
        Assert.Equal(until, snapshot.Authority(privateDraft, AccessOperation.ReadContent, DateTimeOffset.UtcNow).Until);
    }

    [PostgreSqlFact]
    public async Task External_group_grants_keep_the_membership_deadline_in_later_snapshot_decisions()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_grant_deadline_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var (member, group, _) = await SeedAsync(host, false);
        var space = await host.SeedSpaceAsync("Group docs");
        var page = new Page { Id = Guid.NewGuid(), SpaceId = space, OwnerId = member.Id };
        await host.SeedAsync(async s =>
        {
            await s.SaveAsync(page);
            await s.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = space, GroupId = group, Role = 3 });
        });
        using var session = host.OpenSession();
        var authority = await new SpaceRoles(session).AdminAuthorityAsync(member, space, CancellationToken.None);
        Assert.True(authority.Allowed);
        Assert.NotNull(authority.ExpiresAt);
        var snapshot = await new ResourceGrantSnapshots(session).ForPageAsync(member, page, CancellationToken.None);
        Assert.True(snapshot.Evaluate(page, AccessOperation.Share, DateTimeOffset.UtcNow).Allowed);
        Assert.False(snapshot.Evaluate(page, AccessOperation.Share, new DateTimeOffset(authority.ExpiresAt!.Value)).Allowed);
        var vault = new Vault { Id = Guid.NewGuid(), SpaceId = space };
        var vaultSnapshot = await new VaultGrantSnapshots(session).ForVaultsAsync(member, [vault], CancellationToken.None);
        Assert.True(vaultSnapshot.AuthorizeVault(vault, VaultAccessLevel.Read, DateTime.UtcNow).Allowed);
        Assert.Equal(authority.ExpiresAt, vaultSnapshot.AuthorizeVault(vault, VaultAccessLevel.Read, DateTime.UtcNow).Until);
        Assert.False(vaultSnapshot.AuthorizeVault(vault, VaultAccessLevel.Read, authority.ExpiresAt.Value).Allowed);
    }

    internal static async Task<(UserAccount Member, Guid Group, Guid Identity)> SeedAsync(AtlasTestHost host, bool admin)
    {
        var member = await host.SeedUserAsync("hub-member");
        var group = await host.SeedGroupAsync("Hub group", member);
        var identity = Guid.NewGuid();
        var connection = Guid.NewGuid();
        // As the application writes a configured, bound and verified Hub: with the Hub's audit and the binding history (HubHistory).
        await host.SeedAsync(async session =>
        {
            var now = await session.CreateSQLQuery("SELECT statement_timestamp()").UniqueResultAsync<DateTime>();
            await session.SaveAsync(new HubConnection { Id = connection });
            var revision = new HubConnectionRevision { Id = Guid.NewGuid(), ConnectionId = connection, Issuer = "https://hub.example.test/hub",
                BaseUrl = "https://hub.example.test/hub", ProtectedToken = "test-protected", Mode = HubConnectionMode.Enforced, ChangedById = member.Id,
                ActiveFrom = now.AddHours(-1) };
            await session.SaveAsync(revision);
            await HubHistory.ConnectionAsync(session, revision, CancellationToken.None);
            await session.CreateSQLQuery("""
                INSERT INTO "ExternalIdentity"("Id","UserId","Issuer","Subject","ActiveFrom")
                VALUES (:identity,:user,'https://hub.example.test/hub','opaque-user',statement_timestamp()-interval '1 hour')
                """).SetParameter("user", member.Id).SetParameter("identity", identity).ExecuteUpdateAsync();
            var binding = new HubGroupBinding { GroupId = group, ConnectionId = connection, ExternalGroupId = "opaque-group", Enabled = true, GrantsWikiAdmin = admin };
            await session.SaveAsync(binding);
            await HubHistory.BindingAsync(session, binding, null, CancellationToken.None);
            var state = new HubIdentityState { ExternalIdentityId = identity, UserId = member.Id, ConnectionId = connection, ExternalUserId = "opaque-user",
                SnapshotStartedAt = now, VerifiedUntil = now.AddMinutes(5), LastSuccessAt = now, LastAttemptAt = now,
                UpstreamAccountState = HubUpstreamAccountState.Active, Generation = 1 };
            await session.SaveAsync(state);
            await HubHistory.IdentityAsync(session, state, null, null, CancellationToken.None);
        });
        return (member, group, identity);
    }

    internal static Task ExpireAsync(AtlasTestHost host, Guid identity) => host.ExecuteAsync("""
        UPDATE "HubIdentityState" SET "SnapshotStartedAt"=statement_timestamp()-interval '6 minutes',
            "VerifiedUntil"=statement_timestamp()-interval '1 minute' WHERE "ExternalIdentityId"=:id
        """, identity);
}
