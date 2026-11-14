using NHibernate.Criterion;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageAccess.Queries;
using Atlas.Server.Features.Security.Reauth;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Queries;

/// <summary>
/// Access reads of vaults and secrets. The access tab needs Discover on the target (404 otherwise) and shows everyone their own
/// row with its sources; the list of people and groups, the last change and the grant candidates need manage, as the page access
/// tab needs ManageAccess. security/me summarizes the caller's levels in a fixed number of queries.
/// </summary>
public sealed class VaultAccessHandler
{
    private const int CandidateLimit = 20;

    private readonly ISession m_session;
    private readonly VaultAccess m_access;
    private readonly IContentAccess m_content;
    private readonly VaultGrantSnapshots m_grants;
    private readonly FreshAuthMethods m_methods;
    private readonly ReauthProofs m_proofs;

    public VaultAccessHandler(ISession session, VaultAccess access, IContentAccess content, VaultGrantSnapshots grants, FreshAuthMethods methods,
        ReauthProofs proofs)
    {
        m_session = session;
        m_access = access;
        m_content = content;
        m_grants = grants;
        m_methods = methods;
        m_proofs = proofs;
    }

    public async Task<VaultAccessState> Handle(GetVaultAccess query, CancellationToken ct)
    {
        return await StateAsync(await m_access.ReadVaultAsync(query.VaultId, false, ct), ct);
    }

    public async Task<VaultAccessState> Handle(GetSecretAccess query, CancellationToken ct)
    {
        return await StateAsync(await m_access.ReadSecretAsync(query.SecretId, false, ct), ct);
    }

    public async Task<IReadOnlyList<AccessCandidate>> Handle(GetVaultGrantCandidates query, CancellationToken ct)
    {
        var term = CandidateTerm(query.Query);
        (await m_access.ReadVaultAsync(query.VaultId, false, ct)).Authorize(VaultAccessLevel.Manage).Require();
        return await CandidatesAsync(term, ct);
    }

    public async Task<IReadOnlyList<AccessCandidate>> Handle(GetSecretGrantCandidates query, CancellationToken ct)
    {
        var term = CandidateTerm(query.Query);
        (await m_access.ReadSecretAsync(query.SecretId, false, ct)).Authorize(VaultAccessLevel.Manage).Require();
        return await CandidatesAsync(term, ct);
    }

    public async Task<SecurityCapabilitiesView> Handle(GetSecurityCapabilities query, CancellationToken ct)
    {
        var actor = await m_content.LockActorAsync(null, ct);
        var snapshot = await m_grants.ForAccountAsync(actor, ct);
        var now = DateTime.UtcNow;
        // A WikiAdmin manages the vaults of every space (ManagesAllVaults, no space list); everyone else those of the spaces it
        // administers.
        var adminSpaces = actor.IsWikiAdmin ? new HashSet<Guid>() : snapshot.AdminSpaces(now);
        var grantedVaults = snapshot.Grants.Select(g => g.VaultId!.Value).Distinct().ToArray();
        var vaults = actor.IsWikiAdmin ? await m_session.QueryOver<Vault>().Where(v => v.ArchivedAt == null).ListAsync(ct)
            : adminSpaces.Count == 0 && grantedVaults.Length == 0 ? [] : await m_session.QueryOver<Vault>()
            .Where(v => v.ArchivedAt == null)
            .And(Restrictions.Or(
                GrantQueryLimits.InChunks(Projections.Property<Vault>(v => v.SpaceId), adminSpaces.ToArray()),
                GrantQueryLimits.InChunks(Projections.Property<Vault>(v => v.Id), grantedVaults)))
            .ListAsync(ct);
        var summaries = vaults
            .Select(vault => (Vault: vault, Decision: snapshot.AuthorizeVault(vault, VaultAccessLevel.Discover, now)))
            .Where(item => item.Decision.Allowed)
            .Select(item => new VaultAccessSummary(item.Vault.Id, item.Vault.SpaceId, item.Decision.Level.Name()!,
                snapshot.HasSecretGrants(item.Vault.Id, now)))
            .OrderBy(summary => summary.SpaceId).ThenBy(summary => summary.VaultId)
            .ToArray();
        var (support, method) = await m_methods.StateAsync(actor, ct);
        return new SecurityCapabilitiesView(actor.IsWikiAdmin, actor.IsWikiAdmin, adminSpaces.Order().ToArray(), summaries,
            FreshAuthMethod.Name(support), method?.Method, await m_proofs.WindowEndAsync(actor, ct));
    }

    private async Task<VaultAccessState> StateAsync(VaultAccessTarget target, CancellationToken ct)
    {
        target.Authorize(VaultAccessLevel.Discover).Require();
        var vault = target.Vault!;
        var secretId = target.Secret?.Id;
        var space = await m_session.GetAsync<Space>(vault.SpaceId, ct);
        var own = await VaultAccessDirectory.PersonAsync(m_session, vault.SpaceId, vault.Id, secretId, target.Actor.Id, ct);
        if (!target.Authorize(VaultAccessLevel.Manage).Allowed)
        {
            return new VaultAccessState(vault.SpaceId, space.Name, vault.Id, vault.Name, secretId, target.Secret?.Title, false, own, [], false, [], null);
        }
        var (people, hasMore) = await VaultAccessDirectory.PeopleAsync(m_session, vault.SpaceId, vault.Id, secretId, ct);
        return new VaultAccessState(vault.SpaceId, space.Name, vault.Id, vault.Name, secretId, target.Secret?.Title, true, own, people, hasMore,
            await VaultAccessDirectory.GroupsAsync(m_session, vault.SpaceId, vault.Id, secretId, ct),
            await VaultAccessDirectory.LastChangeAsync(m_session, vault.Id, secretId, ct));
    }

    private static string CandidateTerm(string? query)
    {
        if (query?.Length > 200)
        {
            throw new ApiException(400, "Hledejte nejvýše 200 znaků.");
        }
        return query?.Trim() ?? "";
    }

    /// <summary>Active users and all groups by name, the same bounded directory as the page access wizard (PageAccessDirectory).</summary>
    private async Task<IReadOnlyList<AccessCandidate>> CandidatesAsync(string term, CancellationToken ct)
    {
        var users = await PageAccessDirectory.CandidatesAsync(m_session, "user", term, CandidateLimit, ct);
        var groups = await PageAccessDirectory.CandidatesAsync(m_session, "group", term, CandidateLimit, ct);
        return users.Concat(groups).ToArray();
    }
}
