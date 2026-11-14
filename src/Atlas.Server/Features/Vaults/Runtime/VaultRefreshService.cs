using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Queries;
using Atlas.Server.Platform.Jobs;
using Wolverine;

namespace Atlas.Server.Features.Vaults.Runtime;

/// <summary>
/// Refreshes the vaults with a stored password (<see cref="RefreshAllAsync"/>, run by <see cref="VaultRefreshJob"/>) and writes the
/// outcome to the registry: new and changed entries, RemovedAt for entries that disappeared or moved to the recycle bin, restore when
/// they return. File I/O and key derivation run outside any database transaction; each vault's result is one short command.
/// </summary>
public sealed class VaultRefreshService
{
    private readonly IServiceScopeFactory m_scopes;
    private readonly VaultSnapshots m_snapshots;
    private readonly ILogger<VaultRefreshService> m_logger;

    public VaultRefreshService(IServiceScopeFactory scopes, VaultSnapshots snapshots, ILogger<VaultRefreshService> logger)
    {
        m_scopes = scopes;
        m_snapshots = snapshots;
        m_logger = logger;
    }

    /// <summary>One pass over all vaults that are unlocked or failed with a stored password. Locked vaults wait for an administrator.</summary>
    public async Task RefreshAllAsync(CancellationToken ct)
    {
        IReadOnlyList<VaultTarget> targets;
        using (var scope = m_scopes.CreateScope())
        {
            targets = await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<IReadOnlyList<VaultTarget>>(new ListRefreshableVaults(), ct);
        }
        foreach (var target in targets)
        {
            try
            {
                await RefreshAsync(target, false, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                m_logger.LogError("Vault {VaultId} refresh failed ({ErrorType}); the next pass retries.", target.VaultId, error.GetType().Name);
            }
        }
    }

    /// <summary>Refreshes one vault and records the outcome.</summary>
    public async Task<VaultSnapshot> RefreshAsync(VaultTarget target, bool force, CancellationToken ct)
    {
        var snapshot = await m_snapshots.RefreshAsync(target, force, ct);
        using var scope = m_scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(new ApplyVaultSnapshot(target.VaultId, target.ProtectedKdbxPassword, snapshot), ct);
        return snapshot;
    }
}

/// <summary>
/// The job "vaults.refresh": every <see cref="VaultOptions.RefreshSeconds"/> it unlocks every vault with a stored password (the
/// automatic unlock after a restart waits for the first occurrence) and checks the unlocked ones for changes. Registered only when
/// RefreshSeconds is not 0 (tests). The next occurrence is the retry, so a failed pass is not repeated.
/// </summary>
public sealed class VaultRefreshJob(VaultRefreshService refresh, VaultOptions options) : IJobDefinition
{
    public string Id => "vaults.refresh";

    public string Title => "Obnova trezorů";

    public string Description => "Odemkne trezory s uloženým heslem a zapíše změny jejich souborů do registru.";

    public JobSchedule? Schedule => JobSchedule.Every(TimeSpan.FromSeconds(options.RefreshSeconds));

    public JobRetry Retry => JobRetry.None;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        try
        {
            await refresh.RefreshAllAsync(ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The type only: a vault error may quote a path or a password policy.
            throw new JobFailedException($"Vault refresh pass failed ({error.GetType().Name})");
        }
    }
}
