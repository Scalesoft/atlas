using System.Globalization;
using Atlas.Server.Features.Security.Contracts;
using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Security.Networks;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Security.Queries;

/// <summary>Global security reads of every WikiAdmin: the trusted networks, the trusted proxies with the environment they were
/// imported from, the connection diagnostics ("Vaše připojení") and the address tester. All read one configuration version under
/// FOR SHARE.</summary>
public sealed class TrustedAddressQueriesHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_content;
    private readonly IAtlasLocks m_locks;
    private readonly SecurityConfigurationCache m_cache;
    private readonly IHttpContextAccessor m_http;

    public TrustedAddressQueriesHandler(ISession session, IContentAccess content, IAtlasLocks locks, SecurityConfigurationCache cache,
        IHttpContextAccessor http)
    {
        m_session = session;
        m_content = content;
        m_locks = locks;
        m_cache = cache;
        m_http = http;
    }

    public async Task<TrustedNetworkList> Handle(ListTrustedNetworks query, CancellationToken ct)
    {
        var configuration = await ReadAsync(ct);
        return new TrustedNetworkList(Version(configuration), Views(configuration, TrustedAddress.NetworkPurpose));
    }

    public async Task<TrustedProxyList> Handle(ListTrustedProxies query, CancellationToken ct)
    {
        var configuration = await ReadAsync(ct);
        var importedAt = (await m_session.GetAsync<SecurityConfiguration>(SecurityConfiguration.SingletonId, ct))?.ProxiesImportedAt;
        var environment = new ProxyEnvironmentView(m_cache.Environment.Ranges.Select(r => r.Text).ToArray(),
            m_cache.EnvironmentDiffers(configuration), m_cache.Environment.Recover, importedAt);
        return new TrustedProxyList(Version(configuration), configuration.HopLimit, SecurityConfiguration.MaxProxyHops,
            Views(configuration, TrustedAddress.ProxyPurpose), environment);
    }

    public async Task<AddressTestResult> Handle(TestTrustedAddress query, CancellationToken ct)
    {
        var configuration = await ReadAsync(ct);
        var request = query.Request;
        System.Net.IPAddress? address = null;
        string? addressError = null;
        if (!string.IsNullOrWhiteSpace(request.Address) && !AddressRange.TryParseAddress(request.Address, out address))
        {
            addressError = "Toto není platná adresa IPv4 ani IPv6.";
            address = null;
        }
        var networks = configuration.Rules.Where(r => r.Purpose == TrustedAddress.NetworkPurpose && r.Id != request.ExcludeId).ToArray();
        var matched = address is null ? null : new NetworkConfiguration(configuration.Version, configuration.HopLimit, networks).MatchNetwork(address);
        var inactive = address is null ? [] : networks.Where(r => !r.IsActive && r.Range.Contains(address)).Select(Match).ToArray();
        DraftRangeView? draft = null;
        if (!string.IsNullOrWhiteSpace(request.Range))
        {
            var parsed = AddressRange.Parse(request.Range);
            draft = parsed.Range is { } range
                ? new DraftRangeView(range.Text, parsed.Normalized, null, address is not null && range.Contains(address),
                    TrustedAddressRules.Warnings(TrustedAddress.NetworkPurpose, parsed, networks.Where(r => r.IsActive)))
                : new DraftRangeView(null, false, parsed.Error, false, []);
        }
        return new AddressTestResult(address?.ToString(), addressError, address is not null && ClientAddressResolver.IsProxy(address, configuration.ActiveProxies),
            matched is null ? null : Match(matched), inactive, draft);
    }

    public async Task<ConnectionView> Handle(GetConnection query, CancellationToken ct)
    {
        var configuration = await ReadAsync(ct);
        var evidence = ClientAddressEvidence.Of(m_http.HttpContext);
        var client = configuration.Resolve(evidence);
        var network = configuration.MatchNetwork(client.Determined ? client.Address : null);
        var instance = (await ProtectionReads.ChainAsync(m_session, null, null, ct)).Effective;
        return new ConnectionView(client.Address?.ToString(), client.Source.ToString().ToLowerInvariant(), client.Reason, client.Peer?.ToString(),
            client.PeerIsProxy, client.Hops, evidence.ForwardedFor.Select(entry => entry.Length > 100 ? entry[..100] : entry).ToArray(),
            network is null ? null : Match(network), network is not null && instance == ProtectionMode.OutsideTrustedNetworks, instance.Name(),
            ProtectionDecision.Requires(instance, network is not null), Version(configuration));
    }

    private async Task<NetworkConfiguration> ReadAsync(CancellationToken ct)
    {
        var actor = await m_content.LockActorAsync(null, ct);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            throw new ApiException(403, "Sítě a proxy spravuje správce wiki.");
        }
        return await m_cache.CurrentAsync(m_session, m_locks, ct);
    }

    private static TrustedAddressView[] Views(NetworkConfiguration configuration, string purpose)
    {
        var rules = configuration.Rules.Where(r => r.Purpose == purpose).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        return rules.Select(rule => new TrustedAddressView(rule.Id, rule.Name, rule.Kind, rule.Range.Text, rule.IsActive,
                TrustedAddressRules.Warnings(purpose, new AddressRangeParse(rule.Range, false, null), rules.Where(o => o.Id != rule.Id && o.IsActive))))
            .ToArray();
    }

    private static TrustedMatchView Match(TrustedRule rule) => new(rule.Id, rule.Name, rule.Kind, rule.Range.Text);

    private static string Version(NetworkConfiguration configuration) => configuration.Version.ToString(CultureInfo.InvariantCulture);
}
