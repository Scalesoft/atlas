using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Vaults;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Security.Commands;

/// <summary>
/// Changes a protection mode, mirroring the grant handlers: validation, authority (WikiAdmin for the instance, manage on the vault or
/// secret, whose space is held FOR UPDATE), the security change guard (a fresh re-authentication proof for exactly this scope, also
/// inside a trusted network), FOR UPDATE on the security configuration row, CAS on the level's current setting (its ActiveFrom),
/// the no-op rule, then a new configuration version and the close + insert at Temporal.NextBoundary. History and audit
/// (protection.set, protection.cleared) are the rows themselves and TR_ProtectionSetting_Audit, with the locked actor.
/// </summary>
public sealed class SetProtectionHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_content;
    private readonly VaultAccess m_vaults;
    private readonly ISecurityChangeGuard m_guard;

    public SetProtectionHandler(ISession session, IContentAccess content, VaultAccess vaults, ISecurityChangeGuard guard)
    {
        m_session = session;
        m_content = content;
        m_vaults = vaults;
        m_guard = guard;
    }

    public async Task Handle(SetProtection command, CancellationToken ct)
    {
        ProtectionMode? mode = command.Request.Mode is null ? null
            : ProtectionModes.Parse(command.Request.Mode)
            ?? throw new ApiException(400, "Vyberte režim: never (nevyžadovat), outsideTrustedNetworks (mimo důvěryhodné sítě) nebo always (vždy).");
        UserAccount actor;
        Guid? vaultId = null;
        Guid? secretId = null;
        if (command.Level == ProtectionLevel.InstanceLevel)
        {
            if (mode is null)
            {
                throw new ApiException(400, "Výchozí režim instance nelze zdědit; vyberte jeden ze tří režimů.");
            }
            actor = await m_content.LockActorAsync(null, ct);
            if (!actor.IsWikiAdmin)
            {
                throw new ApiException(403, "Výchozí ochranu hesel spravuje správce wiki.");
            }
        }
        else
        {
            var target = command.Level == ProtectionLevel.VaultLevel
                ? await m_vaults.ReadVaultAsync(command.TargetId!.Value, true, ct)
                : await m_vaults.ReadSecretAsync(command.TargetId!.Value, true, ct);
            target.Authorize(VaultAccessLevel.Manage).Require();
            actor = target.Actor;
            vaultId = target.Vault!.Id;
            secretId = target.Secret?.Id;
        }
        var kind = secretId is not null ? "secret.protection" : vaultId is not null ? "vault.protection" : "instance.protection";
        await m_guard.RequireAsync(actor, new SecurityChange(kind, secretId ?? vaultId ?? Guid.Empty), ct);

        var configuration = await m_session.GetAsync<SecurityConfiguration>(SecurityConfiguration.SingletonId, LockMode.Upgrade, ct)
            ?? throw new ApiException(503, "Konfigurace zabezpečení chybí. Zkontrolujte migrace databáze.");
        var (instance, vault, secret) = await ProtectionReads.CurrentAsync(m_session, vaultId, secretId, ct);
        var current = secretId is not null ? secret : vaultId is not null ? vault : instance;
        AdministrationVersion.Expect(current?.ActiveFrom, command.Request.ExpectedVersion);
        if ((current is null && mode is null) || (current is not null && mode is not null && current.Mode == mode.Value.Name()))
        {
            return;
        }
        var last = current?.ActiveFrom ?? await History(vaultId, secretId)
            .Select(Projections.Max<ProtectionSetting>(s => s.ActiveTo))
            .SingleOrDefaultAsync<DateTime?>(ct);
        var boundary = last is null ? DateTime.UtcNow : Temporal.NextBoundary(DateTime.SpecifyKind(last.Value, DateTimeKind.Utc), DateTime.UtcNow);
        configuration.Version++;
        await SecurityHistory.ConfigurationAsync(m_session, configuration, actor.Id, ct);
        if (current is not null)
        {
            current.ActiveTo = boundary;
            // Flushes first: close before insert, NHibernate would flush the insert first and collide with the current row.
            await AccessAuditLog.RecordAsync(m_session, actor.Id, "protection.cleared", AccessAuditLog.ProtectionSetting, current.Id, ct);
        }
        if (mode is { } value)
        {
            var setting = new ProtectionSetting
            {
                Id = Guid.NewGuid(),
                VaultId = vaultId,
                SecretId = secretId,
                Mode = value.Name(),
                ConfigurationVersion = configuration.Version,
                ChangedBy = actor.Id,
                ActiveFrom = boundary
            };
            await m_session.SaveAsync(setting, ct);
            await AccessAuditLog.RecordAsync(m_session, actor.Id, "protection.set", AccessAuditLog.ProtectionSetting, setting.Id, ct);
        }
    }

    private IQueryOver<ProtectionSetting, ProtectionSetting> History(Guid? vaultId, Guid? secretId)
    {
        var query = m_session.QueryOver<ProtectionSetting>();
        query = vaultId is { } vault ? query.Where(s => s.VaultId == vault) : query.Where(s => s.VaultId == null);
        return secretId is { } secret ? query.And(s => s.SecretId == secret) : query.And(s => s.SecretId == null);
    }
}
