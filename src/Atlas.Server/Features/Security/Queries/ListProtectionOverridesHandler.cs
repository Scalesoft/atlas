using Atlas.Server.Features.Security.Contracts;
using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Spaces.Domain;
using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Security.Queries;

/// <summary>"Vlastní nastavení" of Ochrana hesel: every current vault or secret row of ProtectionSetting whose vault is not archived
/// and whose secret is still in the file, with names for the list. Global security, so WikiAdmin only (403 otherwise).</summary>
public sealed class ListProtectionOverridesHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_content;

    public ListProtectionOverridesHandler(ISession session, IContentAccess content)
    {
        m_session = session;
        m_content = content;
    }

    public async Task<IReadOnlyList<ProtectionOverrideView>> Handle(ListProtectionOverrides query, CancellationToken ct)
    {
        var actor = await m_content.LockActorAsync(null, ct);
        if (!actor.IsWikiAdmin)
        {
            throw new ApiException(403, "Výchozí ochranu hesel spravuje správce wiki.");
        }
        var settings = await m_session.QueryOver<ProtectionSetting>().Where(s => s.ActiveTo == null && s.VaultId != null).ListAsync(ct);
        if (settings.Count == 0)
        {
            return [];
        }
        var vaultIds = settings.Select(s => s.VaultId!.Value).Distinct().ToArray();
        var secretIds = settings.Where(s => s.SecretId is not null).Select(s => s.SecretId!.Value).Distinct().ToArray();
        var vaults = (await m_session.QueryOver<Vault>().WhereRestrictionOn(v => v.Id).IsIn(vaultIds).And(v => v.ArchivedAt == null).ListAsync(ct))
            .ToDictionary(v => v.Id);
        var secrets = secretIds.Length == 0
            ? new Dictionary<Guid, VaultSecret>()
            : (await m_session.QueryOver<VaultSecret>().WhereRestrictionOn(s => s.Id).IsIn(secretIds).And(s => s.RemovedAt == null).ListAsync(ct))
                .ToDictionary(s => s.Id);
        var spaceIds = vaults.Values.Select(v => v.SpaceId).Distinct().ToArray();
        var spaces = (await m_session.QueryOver<Space>().WhereRestrictionOn(s => s.Id).IsIn(spaceIds).ListAsync(ct)).ToDictionary(s => s.Id);
        return settings
            .Where(s => vaults.ContainsKey(s.VaultId!.Value) && (s.SecretId is null || secrets.ContainsKey(s.SecretId.Value)))
            .Select(s =>
            {
                var vault = vaults[s.VaultId!.Value];
                var secret = s.SecretId is { } id ? secrets[id] : null;
                return new ProtectionOverrideView(vault.SpaceId, spaces.GetValueOrDefault(vault.SpaceId)?.Name ?? "", vault.Id, vault.Name, secret?.Id,
                    secret?.Title, ProtectionModes.FromStored(s.Mode).Name());
            })
            .OrderBy(v => v.SpaceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(v => v.VaultName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(v => v.SecretTitle is null ? 0 : 1)
            .ThenBy(v => v.SecretTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
