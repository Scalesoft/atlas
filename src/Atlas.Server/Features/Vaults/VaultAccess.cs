using NHibernate;
using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults;

/// <summary>
/// Vault authorization of the request actor. Two separate questions:
/// <list type="bullet">
/// <item>Registry management (V2): WikiAdmin manages locations and every vault registration; a space Admin manages the vault
/// registrations of that space.</item>
/// <item>Value access (V3): one access level (discover, read, manage) decided by <see cref="VaultGrantSnapshot"/>; a WikiAdmin
/// manages every vault and secret, a space Admin those of its space (owner decision 27. 9.).</item>
/// </list>
/// Decisions use the account read back under the actor lock, in the standard order (content lock, space, account) before any
/// vault row lock. Grant writes hold the vault's space FOR UPDATE instead of FOR SHARE, as page grant writes do
/// (IContentAccess), so a decision under the space lock sees a stable set of grants. A vault the actor cannot manage answers 404.
/// </summary>
public sealed class VaultAccess
{
    private const string VaultMissing = "Trezor nebyl nalezen nebo ho nemůžete spravovat.";

    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly SpaceRoles m_roles;
    private readonly VaultGrantSnapshots m_grants;

    public VaultAccess(ISession session, IContentAccess access, IAtlasLocks locks, SpaceRoles roles, VaultGrantSnapshots grants)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_roles = roles;
        m_grants = grants;
    }

    public async Task<UserAccount> RequireWikiAdminAsync(CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(null, ct);
        // Raw IsWikiAdmin, not SpaceRoles.RequireWikiAdminAsync: Hub administrators get no vaults.
        if (!actor.IsWikiAdmin)
        {
            throw new ApiException(403, "Umístění trezorů spravuje správce wiki.");
        }
        return actor;
    }

    /// <summary>The spaces whose vault registrations the actor manages; null means every space (WikiAdmin).</summary>
    public async Task<(UserAccount Actor, IReadOnlySet<Guid>? Spaces)> ManagedSpacesAsync(CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(null, ct);
        if (actor.IsWikiAdmin)
        {
            return (actor, null);
        }
        var roles = await m_roles.EffectiveAsync(actor, ct);
        return (actor, roles.Where(role => role.Value >= SpaceRole.Admin).Select(role => role.Key).ToHashSet());
    }

    /// <summary>FOR SHARE on the space and the account, then Admin on that space (404 otherwise).</summary>
    public async Task<UserAccount> RequireSpaceAdminAsync(Guid spaceId, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(spaceId, ct);
        if (!await LiveSpace.ExistsAsync(m_session, spaceId, ct)
            || !actor.IsWikiAdmin && await m_roles.EffectiveAsync(actor, spaceId, ct) != SpaceRole.Admin)
        {
            throw new ApiException(404, VaultMissing);
        }
        return actor;
    }

    /// <summary>A current vault whose registration the actor manages; <paramref name="mutate"/> locks its row FOR UPDATE after the space and account.</summary>
    public async Task<(UserAccount Actor, Vault Vault)> RequireVaultAsync(Guid vaultId, bool mutate, CancellationToken ct)
    {
        var vault = await m_session.GetAsync<Vault>(vaultId, ct);
        if (vault is null || vault.ArchivedAt is not null)
        {
            throw new ApiException(404, VaultMissing);
        }
        UserAccount actor;
        try
        {
            actor = await RequireSpaceAdminAsync(vault.SpaceId, ct);
        }
        catch (ApiException error) when (error.Status == 404)
        {
            throw new ApiException(404, VaultMissing);
        }
        if (mutate)
        {
            await m_session.RefreshAsync(vault, LockMode.Upgrade, ct);
            if (vault.ArchivedAt is not null)
            {
                throw new ApiException(404, VaultMissing);
            }
        }
        return (actor, vault);
    }

    /// <summary>A vault and the actor's grants on it, read under the vault's space lock (FOR UPDATE for a grant write, otherwise
    /// FOR SHARE). The caller decides with <see cref="VaultAccessTarget.Authorize"/>; an unknown vault is Missing there.</summary>
    public async Task<VaultAccessTarget> ReadVaultAsync(Guid vaultId, bool writeGrants, CancellationToken ct)
    {
        var spaceId = await m_session.QueryOver<Vault>().Where(v => v.Id == vaultId).Select(v => v.SpaceId).SingleOrDefaultAsync<Guid>(ct);
        return await ReadAsync(spaceId, vaultId, null, writeGrants, ct);
    }

    /// <summary>A secret, its vault and the actor's grants on them, locked as <see cref="ReadVaultAsync"/>.</summary>
    public async Task<VaultAccessTarget> ReadSecretAsync(Guid secretId, bool writeGrants, CancellationToken ct)
    {
        var owner = await m_session.CreateSQLQuery("""
            SELECT v."SpaceId", v."Id" FROM "VaultSecret" s JOIN "Vault" v ON v."Id" = s."VaultId" WHERE s."Id" = :secret
            """).SetParameter("secret", secretId).UniqueResultAsync<object[]>(ct);
        return owner is null
            ? await ReadAsync(Guid.Empty, Guid.Empty, secretId, writeGrants, ct)
            : await ReadAsync((Guid)owner[0], (Guid)owner[1], secretId, writeGrants, ct);
    }

    private async Task<VaultAccessTarget> ReadAsync(Guid spaceId, Guid vaultId, Guid? secretId, bool writeGrants, CancellationToken ct)
    {
        if (spaceId == Guid.Empty)
        {
            // An unknown target still authenticates and locks the actor, so a stale sign-in answers 401, not 404.
            return new VaultAccessTarget(await m_access.LockActorAsync(null, ct), null, null, null);
        }
        if (writeGrants)
        {
            await m_locks.AcquireContentSharedAsync(ct);
            await m_locks.LockSpaceForUpdateAsync(spaceId, ct);
        }
        var actor = await m_access.LockActorAsync(spaceId, ct);
        var vault = await m_session.GetAsync<Vault>(vaultId, ct);
        var secret = secretId is { } id ? await m_session.GetAsync<VaultSecret>(id, ct) : null;
        // A vault of a trashed space waits there with it: missing for everyone, its own grants included.
        if (vault is null || (secretId is not null && secret is null) || !await LiveSpace.ExistsAsync(m_session, spaceId, ct))
        {
            return new VaultAccessTarget(actor, null, null, null);
        }
        return new VaultAccessTarget(actor, vault, secret, await m_grants.ForVaultsAsync(actor, [vault], ct));
    }
}

/// <summary>A vault (Secret null) or one of its secrets with the request actor's grants, read under the vault's space lock.
/// Vault null: the target does not exist.</summary>
public sealed record VaultAccessTarget(UserAccount Actor, Vault? Vault, VaultSecret? Secret, VaultGrantSnapshot? Grants)
{
    public VaultAuthorization Authorize(VaultAccessLevel required)
    {
        if (Vault is null || Grants is null)
        {
            return VaultAuthorization.Missing(required);
        }
        var now = DateTime.UtcNow;
        return Secret is null ? Grants.AuthorizeVault(Vault, required, now) : Grants.AuthorizeSecret(Vault, Secret, required, now);
    }
}
