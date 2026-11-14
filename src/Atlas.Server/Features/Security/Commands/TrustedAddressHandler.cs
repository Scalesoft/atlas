using System.Globalization;
using NHibernate;
using Atlas.Server.Features.Security.Contracts;
using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Security.Networks;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Security.Commands;

/// <summary>
/// Global security of every WikiAdmin (owner decision 27. 9.): trusted networks, trusted proxies and the hop limit. Each change is
/// validated, needs a fresh re-authentication proof for the global scope (also inside LAN/VPN), holds the security configuration
/// row FOR UPDATE, checks its Version (the CAS token of all global changes), and activates as the next version together with its
/// history row (TR_TrustedAddress_History) and audit event. A proxy change that would stop trusting the proxy the admin's own
/// request came through is refused, so the GUI cannot cut off its own access; while Http:RecoverTrustedProxies is on, proxies are
/// read-only.
/// </summary>
public sealed class TrustedAddressHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_content;
    private readonly ISecurityChangeGuard m_guard;
    private readonly SecurityConfigurationCache m_cache;
    private readonly IHttpContextAccessor m_http;

    public TrustedAddressHandler(ISession session, IContentAccess content, ISecurityChangeGuard guard, SecurityConfigurationCache cache,
        IHttpContextAccessor http)
    {
        m_session = session;
        m_content = content;
        m_guard = guard;
        m_cache = cache;
        m_http = http;
    }

    public async Task<TrustedAddressSaved> Handle(SaveTrustedAddress command, CancellationToken ct)
    {
        var purpose = Purpose(command.Purpose);
        var request = command.Request;
        var name = TrustedAddressRules.Name(request.Name);
        var kind = TrustedAddressRules.Kind(purpose, request.Kind);
        var parsed = TrustedAddressRules.Range(purpose, request.Range);
        var actor = await RequireAdminAsync(purpose, ct);
        await m_guard.RequireAsync(actor, new SecurityChange(Kind(purpose), command.Id ?? Guid.Empty), ct);
        var configuration = await LockAsync(request.ExpectedVersion, ct);
        var rules = await m_session.QueryOver<TrustedAddress>().Where(a => a.ArchivedAt == null).ListAsync(ct);
        TrustedAddress? address = null;
        if (command.Id is { } id)
        {
            address = rules.SingleOrDefault(a => a.Id == id && a.Purpose == purpose)
                ?? throw new ApiException(404, purpose == TrustedAddress.ProxyPurpose ? "Proxy nebyla nalezena." : "Síť nebyla nalezena.");
        }
        if (rules.Any(a => a.Purpose == purpose && a.Id != address?.Id && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ApiException(409, "Pravidlo s tímto názvem už existuje.");
        }
        var isActive = request.IsActive ?? true;
        var others = Rules(rules.Where(a => a.Id != address?.Id));
        var warnings = TrustedAddressRules.Warnings(purpose, parsed, others.Where(o => o.IsActive));
        if (address is not null && address.Name == name && address.Kind == kind && address.Range == parsed.Range!.Text && address.IsActive == isActive)
        {
            return new TrustedAddressSaved(address.Id, warnings, Version(configuration));
        }
        if (purpose == TrustedAddress.ProxyPurpose)
        {
            RequireOwnProxyKept(Rules(rules), others.Append(new TrustedRule(address?.Id ?? Guid.Empty, purpose, name, null, parsed.Range!, isActive)));
        }
        await ActivateNextAsync(configuration, actor.Id, ct);
        if (address is null)
        {
            address = new TrustedAddress { Id = Guid.NewGuid(), Purpose = purpose };
            Apply(address, name, kind, parsed.Range!, isActive);
            await m_session.SaveAsync(address, ct);
        }
        else
        {
            Apply(address, name, kind, parsed.Range!, isActive);
        }
        await SecurityHistory.AddressAsync(m_session, address, actor.Id, ct);
        return new TrustedAddressSaved(address.Id, warnings, Version(configuration));
    }

    public async Task<string> Handle(ArchiveTrustedAddress command, CancellationToken ct)
    {
        var purpose = Purpose(command.Purpose);
        var actor = await RequireAdminAsync(purpose, ct);
        await m_guard.RequireAsync(actor, new SecurityChange(Kind(purpose), command.Id), ct);
        var configuration = await LockAsync(command.ExpectedVersion, ct);
        var rules = await m_session.QueryOver<TrustedAddress>().Where(a => a.ArchivedAt == null).ListAsync(ct);
        var address = rules.SingleOrDefault(a => a.Id == command.Id && a.Purpose == purpose)
            ?? throw new ApiException(404, purpose == TrustedAddress.ProxyPurpose ? "Proxy nebyla nalezena." : "Síť nebyla nalezena.");
        if (purpose == TrustedAddress.ProxyPurpose)
        {
            RequireOwnProxyKept(Rules(rules), Rules(rules.Where(a => a.Id != address.Id)));
        }
        await ActivateNextAsync(configuration, actor.Id, ct);
        address.ArchivedAt = DateTime.UtcNow;
        await SecurityHistory.AddressAsync(m_session, address, actor.Id, ct);
        return Version(configuration);
    }

    public async Task<string> Handle(SetProxyHopLimit command, CancellationToken ct)
    {
        var hops = command.Request.HopLimit;
        if (hops is < 1 or > SecurityConfiguration.MaxProxyHops)
        {
            throw new ApiException(400, "Počet proxy na cestě musí být 1 až {maxProxyHops}.", args: new Dictionary<string, object?> { ["maxProxyHops"] = SecurityConfiguration.MaxProxyHops });
        }
        var actor = await RequireAdminAsync(TrustedAddress.ProxyPurpose, ct);
        await m_guard.RequireAsync(actor, new SecurityChange("security.proxy_hops", Guid.Empty), ct);
        var configuration = await LockAsync(command.Request.ExpectedVersion, ct);
        if (configuration.ProxyHopLimit != hops)
        {
            configuration.Version++;
            configuration.ProxyHopLimit = (short)hops;
            await SecurityHistory.ConfigurationAsync(m_session, configuration, actor.Id, ct);
        }
        return Version(configuration);
    }

    private static string Purpose(string purpose)
    {
        return purpose is TrustedAddress.NetworkPurpose or TrustedAddress.ProxyPurpose ? purpose : throw new ArgumentOutOfRangeException(nameof(purpose));
    }

    private static string Kind(string purpose) => purpose == TrustedAddress.ProxyPurpose ? "security.proxy" : "security.network";

    private async Task<UserAccount> RequireAdminAsync(string purpose, CancellationToken ct)
    {
        var actor = await m_content.LockActorAsync(null, ct);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            throw new ApiException(403, "Sítě a proxy spravuje správce wiki.");
        }
        if (purpose == TrustedAddress.ProxyPurpose && m_cache.Environment.Recover)
        {
            throw new ApiException(409, "Proxy jsou obnovené z Http:TrustedProxies (Http:RecoverTrustedProxies). Po odebrání tohoto nastavení a restartu je lze znovu upravit.");
        }
        return actor;
    }

    private async Task<SecurityConfiguration> LockAsync(string? expectedVersion, CancellationToken ct)
    {
        var configuration = await m_session.GetAsync<SecurityConfiguration>(SecurityConfiguration.SingletonId, LockMode.Upgrade, ct)
            ?? throw new ApiException(503, "Konfigurace zabezpečení chybí. Zkontrolujte migrace databáze.");
        if (!string.Equals(expectedVersion, Version(configuration), StringComparison.Ordinal))
        {
            throw new ApiException(409, "Nastavení zabezpečení se mezitím změnilo. Obnovte stránku a změnu zopakujte.");
        }
        return configuration;
    }

    /// <summary>The next version is written first, so the trusted address history records the version its change activated.</summary>
    private async Task ActivateNextAsync(SecurityConfiguration configuration, Guid actorId, CancellationToken ct)
    {
        configuration.Version++;
        await SecurityHistory.ConfigurationAsync(m_session, configuration, actorId, ct);
    }

    private void RequireOwnProxyKept(IEnumerable<TrustedRule> before, IEnumerable<TrustedRule> after)
    {
        var peer = AddressRange.Normalize(ClientAddressEvidence.Of(m_http.HttpContext).Peer);
        if (peer is null)
        {
            return;
        }
        static bool Trusts(IEnumerable<TrustedRule> rules, System.Net.IPAddress address)
            => rules.Any(r => r.Purpose == TrustedAddress.ProxyPurpose && r.IsActive && r.Range.Contains(address));
        if (Trusts(before, peer) && !Trusts(after, peer))
        {
            throw new ApiException(409, "Tato změna by přestala důvěřovat proxy, přes kterou jste právě připojeni, a odřízla by vaše připojení. "
                + "Nejdřív přidejte náhradní proxy nebo změnu proveďte z přímého připojení.", "proxy_lockout");
        }
    }

    private static List<TrustedRule> Rules(IEnumerable<TrustedAddress> addresses)
    {
        var rules = new List<TrustedRule>();
        foreach (var address in addresses)
        {
            if (AddressRange.Parse(address.Range).Range is { } range)
            {
                rules.Add(new TrustedRule(address.Id, address.Purpose, address.Name, address.Kind, range, address.IsActive));
            }
        }
        return rules;
    }

    private static void Apply(TrustedAddress address, string name, string? kind, AddressRange range, bool isActive)
    {
        address.Name = name;
        address.Kind = kind;
        address.Range = range.Text;
        address.IsActive = isActive;
    }

    private static string Version(SecurityConfiguration configuration) => configuration.Version.ToString(CultureInfo.InvariantCulture);
}
