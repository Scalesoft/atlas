using Atlas.Server.Features.Security.Commands;
using Atlas.Server.Features.Security.Contracts;
using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Security.Queries;
using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Release;
using Atlas.Server.Features.Vaults.Requests;
using Atlas.Server.Tests.Security;
using Atlas.Server.Tests.Vaults;
using Xunit;
using static Atlas.Server.Tests.Vaults.VaultReleaseTests;

namespace Atlas.Server.Tests.AccessAudit;

/// <summary>
/// The audit and the history of protection modes, trusted addresses, the hop limit, re-authentication, vault locations, access
/// requests and secret releases, pinned while the database triggers wrote them (the migrations of vaults, protection,
/// secret release and the re-authentication window). C# writes them since 3.6; the output must not change.
/// </summary>
public sealed class SecurityAuditPinTests
{
    [PostgreSqlFact]
    public async Task Protection_trusted_addresses_hops_and_reauthentication_audit_as_the_triggers_did()
    {
        await using var f = await SecurityFixture.CreateAsync();
        var mark = await AuditPin.MarkAsync(f.Host.ConnectionString);
        f.SignIn(f.Admin);
        await f.ProveAsync(vaultId: f.VaultId);
        await f.Invoke(new SetProtection("vault", f.VaultId, new UpdateProtection("outsideTrustedNetworks", null)));
        var vault = await f.Invoke<ProtectionState>(new GetProtection("vault", f.VaultId));
        await f.ProveAsync(vaultId: f.VaultId);
        await f.Invoke(new SetProtection("vault", f.VaultId, new UpdateProtection("never", vault.Version)));
        vault = await f.Invoke<ProtectionState>(new GetProtection("vault", f.VaultId));
        await f.ProveAsync(vaultId: f.VaultId);
        await f.Invoke(new SetProtection("vault", f.VaultId, new UpdateProtection(null, vault.Version)));
        await f.ProveAsync();
        var office = await f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(null, TrustedAddress.NetworkPurpose,
            new SaveTrustedAddressRequest("Kancelář", "lan", "192.168.10.0/24", true, "4")));
        // The next change passes in the session's re-authentication window.
        var changed = await f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(office.Id, TrustedAddress.NetworkPurpose,
            new SaveTrustedAddressRequest("Kancelář", "lan", "192.168.10.0/24", false, office.Version)));
        var proxy = await f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(null, TrustedAddress.ProxyPurpose,
            new SaveTrustedAddressRequest("Proxy", null, "10.0.0.10", true, changed.Version)));
        var archived = await f.Invoke<string>(new ArchiveTrustedAddress(office.Id, TrustedAddress.NetworkPurpose, proxy.Version));
        var hops = await f.Invoke<string>(new SetProxyHopLimit(new ProxyHopsRequest(2, archived)));
        await f.Invoke<string>(new SetProxyHopLimit(new ProxyHopsRequest(2, hops)));

        Assert.Equal(
        [
            "reauth.consumed wiki-admin ReauthChallengeId",
            "protection.set wiki-admin ProtectionSettingId",
            "reauth.consumed wiki-admin ReauthChallengeId",
            "protection.cleared wiki-admin ProtectionSettingId",
            "protection.set wiki-admin ProtectionSettingId",
            "reauth.consumed wiki-admin ReauthChallengeId",
            "protection.cleared wiki-admin ProtectionSettingId",
            "reauth.consumed wiki-admin ReauthChallengeId",
            "trusted_network.created wiki-admin TrustedAddressRevisionId",
            "reauth.window_used wiki-admin ReauthChallengeId",
            "trusted_network.updated wiki-admin TrustedAddressRevisionId",
            "reauth.window_used wiki-admin ReauthChallengeId",
            "trusted_proxy.created wiki-admin TrustedAddressRevisionId",
            "reauth.window_used wiki-admin ReauthChallengeId",
            "trusted_network.removed wiki-admin TrustedAddressRevisionId",
            "reauth.window_used wiki-admin ReauthChallengeId",
            "security.proxy_hops_changed wiki-admin SecurityRevisionId",
            "reauth.window_used wiki-admin ReauthChallengeId",
        ], await AuditPin.EventsAsync(f.Host.ConnectionString, mark));
        // Every security change is a version of the configuration; the first one is the migration's.
        Assert.Equal(
        [
            "- closed 1 1",
            "wiki-admin closed 2 1",
            "wiki-admin closed 3 1",
            "wiki-admin closed 4 1",
            "wiki-admin closed 5 1",
            "wiki-admin closed 6 1",
            "wiki-admin closed 7 1",
            "wiki-admin closed 8 1",
            "wiki-admin open 9 2",
        ], await AuditPin.RevisionsAsync(f.Host.ConnectionString, "SecurityConfigurationRevision", "ChangedBy", "Version", "ProxyHopLimit"));
        Assert.Equal(
        [
            "wiki-admin closed Kancelář true false 5",
            "wiki-admin closed Kancelář false false 6",
            "wiki-admin open Proxy true false 7",
            "wiki-admin open Kancelář false true 8",
        ], await AuditPin.RevisionsAsync(f.Host.ConnectionString, "TrustedAddressRevision", "ChangedBy", "Name", "IsActive",
            "r.\"ArchivedAt\" IS NOT NULL", "ConfigurationVersion"));
    }

    [PostgreSqlFact]
    public async Task Vault_locations_access_requests_and_releases_audit_as_the_triggers_did()
    {
        // Own database: AuditPin compares every audit row and location revision written after its mark, not this test's only.
        await using var f = await VaultFixture.CreateAsync(release: new FakeReleasePolicy(), isolated: true);
        var (vault, router, mail) = await AttachAsync(f);
        var asker = await f.Host.SeedUserAsync("asker");
        await Grant(f, vault, asker, "discover");
        var mark = await AuditPin.MarkAsync(f.Host.ConnectionString);

        var first = await f.As<AccessRequestView>(asker, new RequestSecretAccess(router, new CreateAccessRequest("read", "Servis")));
        await f.As<AccessRequestView>(f.SpaceAdmin, new RejectAccessRequest(first.Id, "Ne"));
        var second = await f.As<AccessRequestView>(asker, new RequestSecretAccess(router, new CreateAccessRequest("read", "Znovu")));
        await f.As<AccessRequestView>(f.SpaceAdmin, new ApproveAccessRequest(second.Id, null));
        var third = await f.As<AccessRequestView>(asker, new RequestSecretAccess(mail, new CreateAccessRequest("read", "Pošta")));
        await f.As<AccessRequestView>(asker, new CancelAccessRequest(third.Id));
        await Release(f, asker, router);
        await Assert.ThrowsAnyAsync<ApiException>(() => Release(f, asker, mail));
        await f.As(asker, new ReportSecretRelease(router, ["password"], "copy"));
        var location = await f.As<VaultLocationView>(f.Admin, new SaveVaultLocation(null, new SaveVaultLocationRequest("Druhé", "local", f.Folder)));
        await f.As<VaultLocationView>(f.Admin, new SaveVaultLocation(location.Id, new SaveVaultLocationRequest("Druhé místo", "local", f.Folder)));
        await f.As(f.Admin, new ArchiveVaultLocation(location.Id));

        Assert.Equal(
        [
            "access_request.created asker AccessRequestId",
            "access_request.rejected space-admin AccessRequestId",
            "access_request.created asker AccessRequestId",
            "resource_grant.added space-admin ResourceGrantId",
            "access_request.approved space-admin AccessRequestId",
            "access_request.created asker AccessRequestId",
            "access_request.cancelled asker AccessRequestId",
            "secret.released asker SecretReleaseEventId",
            "secret.release_denied asker SecretReleaseEventId",
            "secret.client_report asker SecretReleaseEventId",
        ], await AuditPin.EventsAsync(f.Host.ConnectionString, mark));
        Assert.Equal(
        [
            "wiki-admin open Místní trezory false false false",
            "wiki-admin closed Druhé false false false",
            "wiki-admin closed Druhé místo false false false",
            "wiki-admin open Druhé místo false false true",
        ], await AuditPin.RevisionsAsync(f.Host.ConnectionString, "VaultLocationRevision", "ChangedBy", "Name", "HasPassword",
            "PasswordChanged", "r.\"ArchivedAt\" IS NOT NULL"));
    }
}
