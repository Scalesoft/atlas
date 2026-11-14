using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Security.Commands;
using Atlas.Server.Features.Security.Contracts;
using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Security.Networks;
using Atlas.Server.Features.Security.Queries;
using Atlas.Server.Features.Security.Reauth;
using Xunit;

namespace Atlas.Server.Tests.Security;

/// <summary>Trusted networks and proxies over real PostgreSQL: WikiAdmin only, fresh re-authentication inside a trusted network too,
/// atomic versioned activation with CAS, history and audit, the tester and diagnostics, the self-lockout guard and the one-time
/// import of Http:TrustedProxies with its recovery switch. Tests that change trusted addresses have a host of their own: TrustedAddress
/// and the SecurityConfiguration version (row Id 1) are the installation's.</summary>
public sealed class TrustedAddressHandlerTests
{
    private static SaveTrustedAddressRequest Network(string name, string range, string version, string kind = "lan", bool active = true)
        => new(name, kind, range, active, version);

    [PostgreSqlFact]
    public async Task Networks_activate_as_versions_with_history_audit_and_cas()
    {
        await using var f = await SecurityFixture.CreateAsync();
        f.SignIn(f.Admin, peer: "192.168.10.5");
        var list = await f.Invoke<TrustedNetworkList>(new ListTrustedNetworks());
        Assert.Equal(("1", 0), (list.Version, list.Items.Count));

        await f.ProveAsync();
        var office = await f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(null, TrustedAddress.NetworkPurpose, Network("Kancelář", "192.168.10.77/24", "1")));
        Assert.Equal("2", office.Version);
        Assert.Contains(office.Warnings, w => w.Contains("192.168.10.0/24"));
        // Inside the new trusted network a security change still needs a fresh re-authentication (owner decision 27. 9.): a session
        // without one is refused.
        f.SignIn(f.Admin, session: "office-desk", peer: "192.168.10.5");
        var required = await Assert.ThrowsAsync<ApiException>(() =>
            f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(null, TrustedAddress.NetworkPurpose, Network("VPN", "10.8.0.0/16", "2", "vpn"))));
        Assert.Equal(ReauthRefusal.RequiredCode, required.Code);
        f.SignIn(f.Admin, peer: "192.168.10.5");
        await f.ProveAsync();
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() =>
            f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(null, TrustedAddress.NetworkPurpose, Network("VPN", "10.8.0.0/16", "1", "vpn"))))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() =>
            f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(null, TrustedAddress.NetworkPurpose, Network("kancelář", "10.9.0.0/16", "2"))))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() =>
            f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(null, TrustedAddress.NetworkPurpose, Network("Wifi", "10.1", "2"))))).Status);
        var vpn = await f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(null, TrustedAddress.NetworkPurpose, Network("VPN", "2001:db8:10::/48", "2", "vpn")));
        Assert.Equal("3", vpn.Version);

        await f.ProveAsync();
        var off = await f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(office.Id, TrustedAddress.NetworkPurpose, Network("Kancelář", "192.168.10.0/24", "3", active: false)));
        Assert.Equal("4", off.Version);
        await f.ProveAsync();
        Assert.Equal("5", await f.Invoke<string>(new ArchiveTrustedAddress(vpn.Id, TrustedAddress.NetworkPurpose, "4")));
        list = await f.Invoke<TrustedNetworkList>(new ListTrustedNetworks());
        Assert.Equal("5", list.Version);
        var item = Assert.Single(list.Items);
        Assert.Equal(("Kancelář", "lan", "192.168.10.0/24", false), (item.Name, item.Kind, item.Range, item.IsActive));

        Assert.Equal(4L, await f.CountAsync("SELECT count(*) FROM \"TrustedAddressRevision\""));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM \"AccessAuditEvent\" WHERE \"Action\"='trusted_network.created' AND \"ActorId\"=:a", ("a", f.Admin.Id)));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM \"AccessAuditEvent\" WHERE \"Action\"='trusted_network.updated'"));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM \"AccessAuditEvent\" WHERE \"Action\"='trusted_network.removed'"));
        Assert.Equal(4L, await f.CountAsync("SELECT \"ConfigurationVersion\" FROM \"TrustedAddressRevision\" WHERE \"AddressId\"=:id AND \"ActiveTo\" IS NULL", ("id", office.Id)));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM \"AccessAuditView\" WHERE \"Action\"='trusted_network.removed' AND \"TrustedAddressName\"='VPN' AND \"ConfigurationVersion\"=5"));
        await Platform.IntegrityRefusal.AssertAsync(f.Host, "Trusted addresses are archived, not deleted",
            async s => await s.DeleteAsync(await s.GetAsync<TrustedAddress>(office.Id)));
        // A hop limit change without a new version is refused (IntegrityRules, C# since 3.6).
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.SeedAsync(async s =>
        {
            (await s.GetAsync<SecurityConfiguration>(SecurityConfiguration.SingletonId))!.ProxyHopLimit = 2;
            await s.FlushAsync();
        }));
        Assert.Equal("A security configuration change must increment Version", refused.Message);
    }

    [PostgreSqlFact]
    public async Task Only_a_wiki_admin_manages_global_security()
    {
        await using var f = await SecurityFixture.CreateAsync(shared: true);
        f.SignIn(f.SpaceAdmin);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<TrustedNetworkList>(new ListTrustedNetworks()))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<TrustedProxyList>(new ListTrustedProxies()))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<ConnectionView>(new GetConnection()))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() =>
            f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(null, TrustedAddress.NetworkPurpose, Network("Moje", "10.0.0.0/8", "1"))))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<string>(new SetProxyHopLimit(new ProxyHopsRequest(2, "1"))))).Status);
    }

    [PostgreSqlFact]
    public async Task Proxies_hops_diagnostics_and_the_tester_follow_the_current_version()
    {
        await using var f = await SecurityFixture.CreateAsync();
        f.SignIn(f.Admin, peer: "10.0.0.10", headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "198.51.100.1, 192.168.10.5" });
        var connection = await f.Invoke<ConnectionView>(new GetConnection());
        Assert.Equal((null, "unclear", ClientAddress.UntrustedForwardingReason, "10.0.0.10"), (connection.Address, connection.Source, connection.Reason, connection.Peer));
        Assert.Equal(("always", true), (connection.InstanceMode, connection.ReauthRequired));

        await f.ProveAsync();
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(null, TrustedAddress.ProxyPurpose,
            new SaveTrustedAddressRequest("Vše", null, "0.0.0.0/0", true, "1"))))).Status);
        var proxy = await f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(null, TrustedAddress.ProxyPurpose,
            new SaveTrustedAddressRequest("Reverzní proxy", null, "10.0.0.10", true, "1")));
        await f.ProveAsync();
        await f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(null, TrustedAddress.NetworkPurpose, Network("Kancelář", "192.168.10.0/24", "2")));
        connection = await f.Invoke<ConnectionView>(new GetConnection());
        Assert.Equal(("192.168.10.5", "forwarded", 1, "Kancelář", "3"),
            (connection.Address, connection.Source, connection.Hops, connection.Network?.Name, connection.Version));
        Assert.Equal(["198.51.100.1", "192.168.10.5"], connection.ForwardedFor);
        Assert.False(connection.NetworkException);

        // The admin sits behind that proxy: removing or deactivating it would cut the admin off.
        await f.ProveAsync();
        var lockout = await Assert.ThrowsAsync<ApiException>(() => f.Invoke<string>(new ArchiveTrustedAddress(proxy.Id, TrustedAddress.ProxyPurpose, "3")));
        Assert.Equal((409, "proxy_lockout"), (lockout.Status, lockout.Code));
        await Assert.ThrowsAsync<ApiException>(() => f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(proxy.Id, TrustedAddress.ProxyPurpose,
            new SaveTrustedAddressRequest("Reverzní proxy", null, "10.0.0.10", false, "3"))));
        Assert.Equal("4", await f.Invoke<string>(new SetProxyHopLimit(new ProxyHopsRequest(2, "3"))));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM \"AccessAuditEvent\" WHERE \"Action\"='security.proxy_hops_changed' AND \"ActorId\"=:a", ("a", f.Admin.Id)));
        await f.ProveAsync();
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<string>(new SetProxyHopLimit(new ProxyHopsRequest(6, "4"))))).Status);
        var proxies = await f.Invoke<TrustedProxyList>(new ListTrustedProxies());
        Assert.Equal(("4", 2, "10.0.0.10/32", false), (proxies.Version, proxies.HopLimit, Assert.Single(proxies.Items).Range, proxies.Environment.Differs));

        var test = await f.Invoke<AddressTestResult>(new TestTrustedAddress(new TestAddressRequest("192.168.10.200", "192.168.10.128/25", null)));
        Assert.Equal(("192.168.10.200", "Kancelář", false), (test.Address, test.Network?.Name, test.IsProxy));
        Assert.True(test.Draft!.ContainsAddress);
        Assert.Contains(test.Draft.Warnings, w => w.Contains("Kancelář"));
        var bad = await f.Invoke<AddressTestResult>(new TestTrustedAddress(new TestAddressRequest("nope", "10.0.0.0/99", null)));
        Assert.NotNull(bad.AddressError);
        Assert.NotNull(bad.Draft!.Error);
        Assert.True((await f.Invoke<AddressTestResult>(new TestTrustedAddress(new TestAddressRequest("10.0.0.10", null, null)))).IsProxy);
    }

    [PostgreSqlFact]
    public async Task A_change_elsewhere_is_seen_at_the_next_evaluation()
    {
        await using var f = await SecurityFixture.CreateAsync();
        f.SignIn(f.Admin, peer: "192.168.10.5");
        await f.ProveAsync();
        await f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(null, TrustedAddress.NetworkPurpose, Network("Kancelář", "192.168.10.0/24", "1")));
        Assert.Equal("Kancelář", (await f.Invoke<ConnectionView>(new GetConnection())).Network?.Name);
        // Another instance deactivates the network: a new committed version, the cache here never learnt of it.
        await f.Host.ExecuteAsync("UPDATE \"SecurityConfiguration\" SET \"Version\"=\"Version\"+1 WHERE \"Id\"=1; " +
            "UPDATE \"TrustedAddress\" SET \"IsActive\"=false WHERE \"Id\"<>:id", Guid.Empty);
        var connection = await f.Invoke<ConnectionView>(new GetConnection());
        Assert.Equal(("3", null), (connection.Version, connection.Network));
    }

    [PostgreSqlFact]
    public async Task Http_trusted_proxies_are_imported_once_without_widening_trust()
    {
        var environment = new[] { IPAddress.Parse("10.0.0.10"), IPAddress.Parse("fd00::10") };
        Guid imported;
        await using (var f = await SecurityFixture.CreateAsync(environment))
        {
            f.SignIn(f.Admin);
            var proxies = await f.Invoke<TrustedProxyList>(new ListTrustedProxies());
            Assert.Equal(["10.0.0.10/32", "fd00::10/128"], proxies.Items.Select(p => p.Range).Order(StringComparer.Ordinal));
            Assert.Equal(("2", 1, false, false), (proxies.Version, proxies.HopLimit, proxies.Environment.Differs, proxies.Environment.Recovery));
            Assert.NotNull(proxies.Environment.ImportedAt);
            Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM \"AccessAuditEvent\" WHERE \"Action\"='trusted_proxy.created' AND \"ActorId\" IS NULL"));
            imported = proxies.Items[0].Id;
            await f.ProveAsync();
            await f.Invoke<string>(new ArchiveTrustedAddress(imported, TrustedAddress.ProxyPurpose, "2"));
            // The transport follows the database, not the environment.
            var cache = f.Host.Services.GetRequiredService<SecurityConfigurationCache>();
            await cache.RefreshTransportAsync();
            var transport = cache.Transport();
            Assert.Single(transport.Proxies);
        }
    }

    [PostgreSqlFact]
    public async Task Imported_once_then_the_database_wins_and_recovery_replaces_visibly()
    {
        await using var first = await SecurityFixture.CreateAsync([IPAddress.Parse("10.0.0.10")]);
        first.SignIn(first.Admin);
        Assert.Equal("2", (await first.Invoke<TrustedProxyList>(new ListTrustedProxies())).Version);
        await first.ProveAsync();
        await first.Invoke<string>(new ArchiveTrustedAddress((await first.Invoke<TrustedProxyList>(new ListTrustedProxies())).Items[0].Id,
            TrustedAddress.ProxyPurpose, "2"));
        // A second server start with the same environment imports nothing again and shows the difference.
        var cache = new SecurityConfigurationCache(first.Host.Database, new TrustedProxyEnvironment([IPAddress.Parse("10.0.0.10")], false),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SecurityConfigurationCache>.Instance);
        using (var session = first.Host.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            var configuration = await cache.CurrentAsync(session, new AtlasLocks(session), CancellationToken.None);
            Assert.Equal((3L, 0), (configuration.Version, configuration.ActiveProxies.Count));
            Assert.True(cache.EnvironmentDiffers(configuration));
        }
        // Recovery puts the environment list back as the next version, with one hop, and is visible.
        var recovery = new SecurityConfigurationCache(first.Host.Database, new TrustedProxyEnvironment([IPAddress.Parse("10.0.0.20")], true),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SecurityConfigurationCache>.Instance);
        using (var session = first.Host.OpenSession())
        using (var tx = session.BeginTransaction())
        {
            var configuration = await recovery.CurrentAsync(session, new AtlasLocks(session), CancellationToken.None);
            Assert.Equal((4L, 1, "10.0.0.20/32"), (configuration.Version, configuration.HopLimit, Assert.Single(configuration.ActiveProxies).Text));
        }
        Assert.Equal(0L, await first.CountAsync("SELECT count(*) FROM \"TrustedAddress\" WHERE \"Range\"='10.0.0.10/32' AND \"ArchivedAt\" IS NULL"));
    }

    [PostgreSqlFact]
    public async Task Recovery_makes_proxies_read_only()
    {
        await using var f = await SecurityFixture.CreateAsync([IPAddress.Parse("10.0.0.10")], recover: true);
        f.SignIn(f.Admin);
        var proxies = await f.Invoke<TrustedProxyList>(new ListTrustedProxies());
        Assert.True(proxies.Environment.Recovery);
        await f.ProveAsync();
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(null, TrustedAddress.ProxyPurpose,
            new SaveTrustedAddressRequest("Další", null, "10.0.0.11", true, proxies.Version))))).Status);
        // Networks stay editable, and nothing about authorization or the proof requirement changes.
        await f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(null, TrustedAddress.NetworkPurpose, Network("Kancelář", "192.168.10.0/24", proxies.Version)));
    }
}
