using NHibernate;
using Atlas.Server.Features.Security.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Security.Networks;

/// <summary>
/// The trusted networks and proxies of the current configuration version, shared by the process.
/// <list type="bullet">
/// <item>Decisions (<see cref="CurrentAsync"/>) run in the caller's transaction: FOR SHARE on the SecurityConfiguration row, then the
/// cached rules when their version is still the current one, otherwise a reload in that transaction. A change committed by another
/// instance is therefore never missed.</item>
/// <item>The transport (<see cref="Transport"/>: client address and scheme for rate limits, logs and HTTPS) uses the last loaded
/// version, refreshed at most every 30 seconds and right after a local change (<see cref="Invalidate"/>). Until the database
/// answers it trusts the environment's Http:TrustedProxies as before this slice; security decisions never do.</item>
/// </list>
/// The first load on a schema imports Http:TrustedProxies once (<see cref="TrustedProxyEnvironment"/>). Infrastructure of the
/// request pipeline, so the transport side opens its own session and transaction, like the OIDC token store.
/// </summary>
public sealed class SecurityConfigurationCache
{
    private const long TransportRefreshMilliseconds = 30_000;
    private readonly AtlasDatabase m_database;
    private readonly TrustedProxyEnvironment m_environment;
    private readonly ILogger<SecurityConfigurationCache> m_log;
    // Not disposed: without a wait handle SemaphoreSlim holds no resource, and a background refresh may outlive the container.
    private readonly SemaphoreSlim m_importGate = new(1, 1);
    private readonly SemaphoreSlim m_transportGate = new(1, 1);
    private NetworkConfiguration? m_current;
    private long m_transportRefreshDue;
    private bool m_imported;

    public SecurityConfigurationCache(AtlasDatabase database, TrustedProxyEnvironment environment, ILogger<SecurityConfigurationCache> log)
    {
        m_database = database;
        m_environment = environment;
        m_log = log;
    }

    public TrustedProxyEnvironment Environment => m_environment;

    /// <summary>The current configuration under FOR SHARE on its row, in the caller's transaction.</summary>
    public async Task<NetworkConfiguration> CurrentAsync(ISession session, IAtlasLocks locks, CancellationToken ct)
    {
        await EnsureImportedAsync(ct);
        var (version, hopLimit) = await locks.ReadSecurityConfigurationSharedAsync(ct);
        var cached = Volatile.Read(ref m_current);
        if (cached is not null && cached.Version == version)
        {
            return cached;
        }
        var rows = await session.QueryOver<TrustedAddress>().Where(a => a.ArchivedAt == null).ListAsync(ct);
        var rules = new List<TrustedRule>();
        foreach (var row in rows)
        {
            if (AddressRange.Parse(row.Range).Range is { } range)
            {
                rules.Add(new TrustedRule(row.Id, row.Purpose, row.Name, row.Kind, range, row.IsActive));
            }
            else
            {
                // The CHECK constraint makes this unreachable; an unreadable rule is simply not trusted.
                m_log.LogWarning("Trusted address {AddressId} has an unreadable range and is ignored.", row.Id);
            }
        }
        var loaded = new NetworkConfiguration(version, hopLimit, rules);
        Store(loaded);
        return loaded;
    }

    /// <summary>Proxy trust for the transport; see the class remarks. Never waits for the database: a due refresh runs in the
    /// background (outside the request's execution context, so its statements are not the request's), and until the first load
    /// completes the environment list applies.</summary>
    public TransportTrust Transport()
    {
        var current = Volatile.Read(ref m_current);
        if (m_database.Initialized && System.Environment.TickCount64 >= Interlocked.Read(ref m_transportRefreshDue) && m_transportGate.Wait(0))
        {
            Interlocked.Exchange(ref m_transportRefreshDue, System.Environment.TickCount64 + TransportRefreshMilliseconds);
            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(() => RefreshTransportAsync(true));
            }
        }
        return current is null ? new TransportTrust(m_environment.Ranges, 1) : new TransportTrust(current.ActiveProxies, current.HopLimit);
    }

    /// <summary>Loads the current configuration in its own transaction; failures keep the last known proxies.</summary>
    public async Task RefreshTransportAsync(bool gateHeld = false)
    {
        if (!gateHeld)
        {
            await m_transportGate.WaitAsync();
        }
        try
        {
            using var session = m_database.OpenSession();
            using var transaction = session.BeginTransaction();
            await CurrentAsync(session, new AtlasLocks(session), CancellationToken.None);
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            m_log.LogWarning("Trusted proxies could not be refreshed ({ErrorType}); the last known ones stay in use.", ex.GetType().Name);
        }
        finally
        {
            m_transportGate.Release();
        }
    }

    /// <summary>A security change committed here: the next request refreshes the transport.</summary>
    public void Invalidate()
    {
        Interlocked.Exchange(ref m_transportRefreshDue, 0);
    }

    /// <summary>Whether the explicit environment list disagrees with the active database proxies (shown in the GUI, never applied).</summary>
    public bool EnvironmentDiffers(NetworkConfiguration configuration)
    {
        return m_environment.Ranges.Count > 0
            && !m_environment.Ranges.Select(r => r.Text).ToHashSet().SetEquals(configuration.ActiveProxies.Select(r => r.Text));
    }

    private void Store(NetworkConfiguration loaded)
    {
        var cached = Volatile.Read(ref m_current);
        if (cached is null || cached.Version <= loaded.Version)
        {
            Volatile.Write(ref m_current, loaded);
        }
    }

    /// <summary>
    /// Once per schema: the explicit Http:TrustedProxies addresses become exact /32 or /128 proxies with the existing single hop, so
    /// trust is never widened. With recovery on, once per process start: the database proxies are replaced by the environment list.
    /// Its own transaction under FOR UPDATE on the configuration row, with no actor (a system change in the history and audit).
    /// </summary>
    private async Task EnsureImportedAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref m_imported))
        {
            return;
        }
        await m_importGate.WaitAsync(ct);
        try
        {
            if (m_imported)
            {
                return;
            }
            using (var session = m_database.OpenSession())
            using (var transaction = session.BeginTransaction())
            {
                var configuration = await session.GetAsync<SecurityConfiguration>(SecurityConfiguration.SingletonId, LockMode.Upgrade, ct)
                    ?? throw new ApiException(503, "Konfigurace zabezpečení chybí. Zkontrolujte migrace databáze.");
                if (configuration.ProxiesImportedAt is null || m_environment.Recover)
                {
                    await ImportAsync(session, configuration, ct);
                }
                await transaction.CommitAsync(ct);
            }
            Volatile.Write(ref m_imported, true);
        }
        finally
        {
            m_importGate.Release();
        }
    }

    private async Task ImportAsync(ISession session, SecurityConfiguration configuration, CancellationToken ct)
    {
        var existing = await session.QueryOver<TrustedAddress>()
            .Where(a => a.ArchivedAt == null && a.Purpose == TrustedAddress.ProxyPurpose).ListAsync(ct);
        var now = DateTime.UtcNow;
        var recovering = m_environment.Recover && configuration.ProxiesImportedAt is not null;
        var added = m_environment.Ranges.Where(range => recovering || existing.All(e => e.Range != range.Text)).ToArray();
        if (added.Length > 0 || recovering)
        {
            configuration.Version++;
            if (recovering)
            {
                configuration.ProxyHopLimit = 1;
            }
            // The version row first, so the trusted address history records the version it activated. The start has no actor.
            await SecurityHistory.ConfigurationAsync(session, configuration, null, ct);
        }
        configuration.ProxiesImportedAt ??= now;
        if (recovering)
        {
            foreach (var proxy in existing)
            {
                proxy.ArchivedAt = now;
                await SecurityHistory.AddressAsync(session, proxy, null, ct);
            }
            await session.FlushAsync(ct);
            m_log.LogWarning("Http:RecoverTrustedProxies replaced the trusted proxies with {Count} address(es) from Http:TrustedProxies. "
                + "Remove the setting once access works; the proxies stay read-only in the GUI until then.", added.Length);
        }
        foreach (var range in added)
        {
            var proxy = new TrustedAddress
            {
                Id = Guid.NewGuid(),
                Purpose = TrustedAddress.ProxyPurpose,
                Name = "Http:TrustedProxies " + range.Network,
                Range = range.Text,
                IsActive = true
            };
            await session.SaveAsync(proxy, ct);
            await SecurityHistory.AddressAsync(session, proxy, null, ct);
        }
        if (added.Length > 0 && !recovering)
        {
            m_log.LogInformation("Imported {Count} trusted proxy address(es) from Http:TrustedProxies; the database is authoritative from now on.", added.Length);
        }
    }
}
