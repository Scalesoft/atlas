using Atlas.Server.Features.Security.Contracts;
using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Vaults;

namespace Atlas.Server.Features.Security.Queries;

/// <summary>
/// The instance protection is global security (WikiAdmin, 403 otherwise). A vault's or secret's protection is shown to everyone
/// who can Discover it (404 otherwise), so a reader learns whether a release will ask for a new sign-in; the impact counts and
/// editing need manage (Spravuje).
/// </summary>
public sealed class GetProtectionHandler
{
    private readonly IContentAccess m_content;
    private readonly VaultAccess m_vaults;
    private readonly ProtectionStates m_states;

    public GetProtectionHandler(IContentAccess content, VaultAccess vaults, ProtectionStates states)
    {
        m_content = content;
        m_vaults = vaults;
        m_states = states;
    }

    public async Task<ProtectionState> Handle(GetProtection query, CancellationToken ct)
    {
        if (query.Level == ProtectionLevel.InstanceLevel)
        {
            var actor = await m_content.LockActorAsync(null, ct);
            if (!actor.IsWikiAdmin)
            {
                throw new ApiException(403, "Výchozí ochranu hesel spravuje správce wiki.");
            }
            return await m_states.BuildAsync(actor, null, null, true, ct);
        }
        var target = query.Level == ProtectionLevel.VaultLevel
            ? await m_vaults.ReadVaultAsync(query.TargetId!.Value, false, ct)
            : await m_vaults.ReadSecretAsync(query.TargetId!.Value, false, ct);
        target.Authorize(VaultAccessLevel.Discover).Require();
        return await m_states.BuildAsync(target.Actor, target.Vault, target.Secret, target.Authorize(VaultAccessLevel.Manage).Allowed, ct);
    }
}
