using NHibernate.Criterion;
using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Security;

/// <summary>
/// Current protection settings. Callers hold FOR SHARE (or FOR UPDATE) on the SecurityConfiguration row first, so the settings they
/// read belong to one configuration version.
/// </summary>
public static class ProtectionReads
{
    /// <summary>The current rows of the instance and, when given, of the vault and the secret, in one query.</summary>
    public static async Task<(ProtectionSetting? Instance, ProtectionSetting? Vault, ProtectionSetting? Secret)> CurrentAsync(ISession session,
        Guid? vaultId, Guid? secretId, CancellationToken ct)
    {
        var levels = Restrictions.Disjunction().Add(Restrictions.IsNull(Projections.Property<ProtectionSetting>(s => s.VaultId)));
        if (vaultId is { } vault)
        {
            levels.Add(Restrictions.And(Restrictions.Eq(Projections.Property<ProtectionSetting>(s => s.VaultId), vault),
                secretId is { } secret
                    ? Restrictions.Or(Restrictions.IsNull(Projections.Property<ProtectionSetting>(s => s.SecretId)),
                        Restrictions.Eq(Projections.Property<ProtectionSetting>(s => s.SecretId), secret))
                    : Restrictions.IsNull(Projections.Property<ProtectionSetting>(s => s.SecretId))));
        }
        var rows = await session.QueryOver<ProtectionSetting>().Where(s => s.ActiveTo == null).And(levels).ListAsync(ct);
        return (rows.SingleOrDefault(r => r.VaultId is null),
            vaultId is null ? null : rows.SingleOrDefault(r => r.VaultId == vaultId && r.SecretId is null),
            secretId is null ? null : rows.SingleOrDefault(r => r.SecretId == secretId));
    }

    public static ProtectionChain Chain(ProtectionSetting? instance, Guid? vaultId, ProtectionSetting? vault, Guid? secretId, ProtectionSetting? secret)
    {
        return ProtectionChain.Of(Mode(instance), vaultId, Mode(vault), secretId, Mode(secret));
    }

    /// <summary>The chain of a secret (instance, vault, secret), of a vault (instance, vault) or of the instance alone.</summary>
    public static async Task<ProtectionChain> ChainAsync(ISession session, Guid? vaultId, Guid? secretId, CancellationToken ct)
    {
        var (instance, vault, secret) = await CurrentAsync(session, vaultId, secretId, ct);
        return Chain(instance, vaultId, vault, secretId, secret);
    }

    /// <summary>What a change of this level affects: the vaults and current secrets whose effective mode comes from it now or would
    /// once it inherits (instance: vaults without their own mode and their secrets without one; vault: its secrets without one;
    /// secret: itself).</summary>
    public static async Task<(int Vaults, int Secrets)> ImpactAsync(ISession session, Guid? vaultId, Guid? secretId, CancellationToken ct)
    {
        if (secretId is not null)
        {
            return (0, 1);
        }
        var ownSecret = QueryOver.Of<ProtectionSetting>()
            .Where(s => s.ActiveTo == null && s.SecretId != null)
            .Select(s => s.SecretId);
        var secrets = session.QueryOver<VaultSecret>().Where(s => s.RemovedAt == null)
            .WithSubquery.WhereProperty(s => s.Id).NotIn(ownSecret);
        if (vaultId is { } vault)
        {
            return (0, await secrets.And(s => s.VaultId == vault).RowCountAsync(ct));
        }
        var ownVault = QueryOver.Of<ProtectionSetting>()
            .Where(s => s.ActiveTo == null && s.VaultId != null && s.SecretId == null)
            .Select(s => s.VaultId);
        var inheritingVaults = QueryOver.Of<Vault>().Where(v => v.ArchivedAt == null)
            .WithSubquery.WhereProperty(v => v.Id).NotIn(ownVault)
            .Select(v => v.Id);
        var vaults = await session.QueryOver<Vault>().Where(v => v.ArchivedAt == null)
            .WithSubquery.WhereProperty(v => v.Id).NotIn(ownVault).RowCountAsync(ct);
        return (vaults, await secrets.WithSubquery.WhereProperty(s => s.VaultId).In(inheritingVaults).RowCountAsync(ct));
    }

    private static ProtectionMode? Mode(ProtectionSetting? setting)
    {
        return setting is null ? null : ProtectionModes.FromStored(setting.Mode);
    }
}
