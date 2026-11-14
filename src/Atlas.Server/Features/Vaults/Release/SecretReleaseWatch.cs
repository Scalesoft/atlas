using System.Collections.Concurrent;
using Atlas.Server.Features.Jobs;
using Atlas.Server.Platform.Jobs;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Release;

/// <summary>
/// Live revocation of released values (WP4 V5). Each successful release is remembered in memory for as long as the client may show
/// the value (<see cref="SecretReleaseOptions.ValueSeconds"/>); no value is kept, only who released which secret. After every
/// committed access change (AccessChanges: grants, groups, accounts, archived vaults, rejected requests) and every
/// <see cref="SecretReleaseOptions.RevocationCheckSeconds"/> while something is remembered (grant expiry has no commit), the holders
/// are re-authorized for Čte; whoever lost it gets secretAccessRevoked {secretId} so the UI hides the value at once. The next
/// release re-authorizes anyway. One process, like the SignalR hubs; a restart forgets nothing that is still shown for long.
/// </summary>
public sealed class SecretReleaseWatch : BackgroundService, IAccessChangeObserver
{
    private readonly ConcurrentDictionary<(Guid UserId, Guid SecretId), DateTime> m_released = new();
    private readonly SemaphoreSlim m_gate = new(1, 1);
    private readonly IServiceScopeFactory m_scopes;
    private readonly ISecurityEvents m_events;
    private readonly SecretReleaseOptions m_options;
    private readonly ILogger<SecretReleaseWatch> m_logger;
    private readonly SystemServiceHealth m_health;

    public SecretReleaseWatch(IServiceScopeFactory scopes, ISecurityEvents events, SecretReleaseOptions options, ILogger<SecretReleaseWatch> logger,
        SystemServiceHealth health)
    {
        m_health = health;
        m_scopes = scopes;
        m_events = events;
        m_options = options;
        m_logger = logger;
    }

    /// <summary>The account holds a value of the secret until the client lifetime ends.</summary>
    public void Released(Guid userId, Guid secretId)
    {
        m_released[(userId, secretId)] = DateTime.UtcNow.AddSeconds(m_options.ValueSeconds);
    }

    public int Watched => m_released.Count;

    public Task AccessChangedAsync() => RecheckAsync(CancellationToken.None);

    /// <summary>Re-authorizes every value still shown and notifies the accounts that lost Čte. Serialized; cheap when nothing is shown.</summary>
    public async Task RecheckAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var (key, until) in m_released)
        {
            if (until <= now)
            {
                m_released.TryRemove(new KeyValuePair<(Guid, Guid), DateTime>(key, until));
            }
        }
        if (m_released.IsEmpty)
        {
            return;
        }
        await m_gate.WaitAsync(ct);
        try
        {
            using var scope = m_scopes.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<ISession>();
            var grants = scope.ServiceProvider.GetRequiredService<VaultGrantSnapshots>();
            foreach (var byUser in m_released.Keys.GroupBy(key => key.UserId))
            {
                var account = await session.GetAsync<UserAccount>(byUser.Key, ct);
                var secrets = byUser.Select(key => key.SecretId).ToArray();
                var decisions = account is null
                    ? secrets.ToDictionary(id => id, _ => VaultAuthorization.Missing(VaultAccessLevel.Read))
                    : await grants.ForSecretsAsync(account, secrets, VaultAccessLevel.Read, ct);
                foreach (var secretId in secrets.Where(id => !decisions[id].Allowed))
                {
                    if (m_released.TryRemove((byUser.Key, secretId), out _))
                    {
                        await m_events.SecretAccessRevokedAsync(byUser.Key, new SecretAccessRevoked(secretId));
                    }
                }
                session.Clear();
            }
        }
        finally
        {
            m_gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(m_options.RevocationCheckSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    // Nothing shown is a good pass too (RecheckAsync returns at once), so the loop beats while it idles.
                    await RecheckAsync(stoppingToken);
                    m_health.Beat(SystemServices.SecretRelease);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    m_logger.LogWarning("Released secret re-check failed ({ErrorType}); the next pass retries.", error.GetType().Name);
                    m_health.Failed(SystemServices.SecretRelease, error);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            m_logger.LogDebug("Released secret watch stopped");
        }
    }
}
