using NHibernate.Criterion;
using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults;

/// <summary>
/// The trusted input of vault and secret decisions for one account: its current space Admin grants and its current vault and
/// secret grants (direct or through a current group), as loaded by <see cref="VaultGrantSnapshots"/>. Pure evaluation (owner
/// decisions 27. 9.): a space Admin of the vault's space has Manage on all vaults and secrets of the space; anyone else has the
/// highest level among the vault grants and, on a secret, that secret's grants. A WikiAdmin manages every vault and secret, like a space Admin
/// everywhere (owner decision 27. 9., as WikiAdmin is Admin of every space for pages); page roles count for nothing.
/// Validity is checked again at the application clock, so a grant counts only when both clocks agree (architecture 4.2).
/// </summary>
public sealed record VaultGrantSnapshot(UserAccount Actor, IReadOnlyList<SpaceGrant> AdminGrants, IReadOnlyList<ResourceGrant> Grants,
    IReadOnlyDictionary<Guid, DateTime?>? GroupDeadlines = null)
{
    public static VaultGrantSnapshot Empty(UserAccount actor) => new(actor, [], []);

    /// <summary>The vault itself. A secret grant on one of its (current) secrets gives the vault Discover only, so the account can
    /// open the vault to find that secret; any higher level comes from the space Admin role or vault grants.</summary>
    public VaultAuthorization AuthorizeVault(Vault vault, VaultAccessLevel required, DateTime now)
    {
        if (!Actor.IsActive || vault.ArchivedAt is not null)
        {
            return VaultAuthorization.Missing(required);
        }
        var decision = Decide(vault.SpaceId, CurrentGrants(vault.Id, now).Where(g => g.SecretId is null).ToArray(), required, now);
        if (decision.Level == VaultAccessLevel.None && HasSecretGrants(vault.Id, now))
        {
            var secretGrants = CurrentGrants(vault.Id, now).Where(g => g.SecretId is not null).ToArray();
            return Decide(VaultAccessLevel.Discover, false, [], secretGrants.Select(g => (g, VaultAccessLevel.Discover)).ToArray(), required);
        }
        return decision;
    }

    /// <summary>Whether grants on individual (current) secrets of the vault reach the account.</summary>
    public bool HasSecretGrants(Guid vaultId, DateTime now)
    {
        return CurrentGrants(vaultId, now).Any(g => g.SecretId is not null);
    }

    /// <summary>One secret of the vault: the space Admin role, the vault grants and the secret's own grants.</summary>
    public VaultAuthorization AuthorizeSecret(Vault vault, VaultSecret secret, VaultAccessLevel required, DateTime now)
    {
        if (!Actor.IsActive || vault.ArchivedAt is not null || secret.RemovedAt is not null || secret.VaultId != vault.Id)
        {
            return VaultAuthorization.Missing(required);
        }
        var grants = CurrentGrants(vault.Id, now).Where(g => g.SecretId is null || g.SecretId == secret.Id).ToArray();
        return Decide(vault.SpaceId, grants, required, now);
    }

    /// <summary>Spaces in which the account is a space Admin now, the vault managers of owner decision 2.</summary>
    public IReadOnlySet<Guid> AdminSpaces(DateTime now)
    {
        return AdminGrants.Where(g => IsCurrent(g.StartAt, Deadline(g.GroupId, g.ExpiresAt), now)).Select(g => g.SpaceId).ToHashSet();
    }

    private VaultAuthorization Decide(Guid spaceId, IReadOnlyList<ResourceGrant> grants, VaultAccessLevel required, DateTime now)
    {
        if (Actor.IsWikiAdmin)
        {
            var outcome = required == VaultAccessLevel.None ? VaultAccessOutcome.Forbidden : VaultAccessOutcome.Allowed;
            return new VaultAuthorization(outcome, required, VaultAccessLevel.Manage, false, true, [], null);
        }
        var admin = AdminGrants.Where(g => g.SpaceId == spaceId && IsCurrent(g.StartAt, Deadline(g.GroupId, g.ExpiresAt), now)).ToArray();
        var levels = grants.Select(g => (g, VaultAccessLevels.FromRole(g.Role))).ToArray();
        var level = admin.Length > 0 ? VaultAccessLevel.Manage : levels.Select(x => x.Item2).DefaultIfEmpty().Max();
        return Decide(level, admin.Length > 0, admin, levels, required);
    }

    private VaultAuthorization Decide(VaultAccessLevel level, bool spaceAdmin, IReadOnlyList<SpaceGrant> admin,
        IReadOnlyList<(ResourceGrant Grant, VaultAccessLevel Level)> grants, VaultAccessLevel required)
    {
        if (required == VaultAccessLevel.None || level < required)
        {
            var outcome = level.CanDiscover() ? VaultAccessOutcome.Forbidden : VaultAccessOutcome.Missing;
            return new VaultAuthorization(outcome, required, level, spaceAdmin, false, [], null);
        }
        var sources = admin.Select(g => (g.Id, ExpiresAt: Deadline(g.GroupId, g.ExpiresAt)))
            .Concat(grants.Where(g => g.Level >= required).Select(g => (g.Grant.Id, ExpiresAt: Deadline(g.Grant.GroupId, g.Grant.ExpiresAt))))
            .ToArray();
        var until = sources.Any(s => s.ExpiresAt is null) ? null : sources.Max(s => s.ExpiresAt);
        return new VaultAuthorization(VaultAccessOutcome.Allowed, required, level, spaceAdmin, false, sources.Select(s => s.Id).ToArray(), until);
    }

    private IEnumerable<ResourceGrant> CurrentGrants(Guid vaultId, DateTime now)
    {
        return Grants.Where(g => g.VaultId == vaultId && g.ActiveTo is null && IsCurrent(g.StartAt, Deadline(g.GroupId, g.ExpiresAt), now));
    }

    private DateTime? Deadline(Guid? group, DateTime? end) => GroupDeadlines is null ? end : GrantQueryLimits.Deadline(group, end, GroupDeadlines);

    /// <summary>Inclusive start, exclusive expiry, as EffectiveSpaceGrant and EffectiveResourceGrant.</summary>
    private static bool IsCurrent(DateTime? startAt, DateTime? expiresAt, DateTime now)
    {
        return (startAt is null || startAt <= now) && (expiresAt is null || now < expiresAt);
    }
}

/// <summary>Loads <see cref="VaultGrantSnapshot"/> for an explicit account, in the style of ResourceGrantSnapshots, so paths that
/// authorize another account than the request actor (the collaboration guard of V6) decide the same way.</summary>
/// <remarks>Only current assignments valid at the database statement clock are loaded, matched against the account and its current
/// groups in the same statement (GrantQueryLimits). Secret grants of removed secrets are not loaded, so they neither reach a secret
/// nor reveal its vault. Nothing is loaded for an inactive account or a WikiAdmin, who needs no grant. Grants of archived vaults are
/// loaded but never decide anything.</remarks>
public sealed class VaultGrantSnapshots
{
    private readonly ISession m_session;

    public VaultGrantSnapshots(ISession session)
    {
        m_session = session;
    }

    /// <summary>Several vaults in two queries: the space Admin grants of their spaces and the grants of the vaults.</summary>
    public async Task<VaultGrantSnapshot> ForVaultsAsync(UserAccount actor, IReadOnlyCollection<Vault> vaults, CancellationToken ct)
    {
        if (!actor.IsActive || actor.IsWikiAdmin || vaults.Count == 0)
        {
            return VaultGrantSnapshot.Empty(actor);
        }
        var spaceIds = vaults.Select(v => v.SpaceId).Distinct().ToArray();
        var vaultIds = vaults.Select(v => v.Id).Distinct().ToArray();
        var admin = await AdminGrants(actor)
            .And(GrantQueryLimits.InChunks(Projections.Property<SpaceGrant>(g => g.SpaceId), spaceIds))
            .ListAsync(ct);
        var grants = await VaultGrants(actor)
            .And(GrantQueryLimits.InChunks(Projections.Property<ResourceGrant>(g => g.VaultId), vaultIds))
            .ListAsync(ct);
        return new VaultGrantSnapshot(actor, admin.ToArray(), grants.ToArray(), await GrantQueryLimits.GroupDeadlinesAsync(m_session, actor.Id, ct));
    }

    /// <summary>Every space Admin grant and every vault or secret grant of the account, for GET /api/security/me.</summary>
    public async Task<VaultGrantSnapshot> ForAccountAsync(UserAccount actor, CancellationToken ct)
    {
        if (!actor.IsActive || actor.IsWikiAdmin)
        {
            return VaultGrantSnapshot.Empty(actor);
        }
        var admin = await AdminGrants(actor).ListAsync(ct);
        var grants = await VaultGrants(actor).ListAsync(ct);
        return new VaultGrantSnapshot(actor, admin.ToArray(), grants.ToArray(), await GrantQueryLimits.GroupDeadlinesAsync(m_session, actor.Id, ct));
    }

    public async Task<VaultAuthorization> AuthorizeVaultAsync(UserAccount actor, Guid vaultId, VaultAccessLevel required, CancellationToken ct)
    {
        var vault = await m_session.GetAsync<Vault>(vaultId, ct);
        if (vault is null)
        {
            return VaultAuthorization.Missing(required);
        }
        return (await ForVaultsAsync(actor, [vault], ct)).AuthorizeVault(vault, required, DateTime.UtcNow);
    }

    public async Task<VaultAuthorization> AuthorizeSecretAsync(UserAccount actor, Guid secretId, VaultAccessLevel required, CancellationToken ct)
    {
        return (await ForSecretsAsync(actor, [secretId], required, ct))[secretId];
    }

    /// <summary>Secrets of any vaults in four queries (secrets, vaults, space Admin grants, vault grants); an unknown id is Missing.</summary>
    public async Task<IReadOnlyDictionary<Guid, VaultAuthorization>> ForSecretsAsync(UserAccount actor, IReadOnlyCollection<Guid> secretIds,
        VaultAccessLevel required, CancellationToken ct)
    {
        var ids = secretIds.Distinct().ToArray();
        var result = ids.ToDictionary(id => id, _ => VaultAuthorization.Missing(required));
        if (ids.Length == 0 || !actor.IsActive)
        {
            return result;
        }
        var secrets = await m_session.QueryOver<VaultSecret>()
            .Where(GrantQueryLimits.InChunks(Projections.Property<VaultSecret>(s => s.Id), ids))
            .ListAsync(ct);
        var vaultIds = secrets.Select(s => s.VaultId).Distinct().ToArray();
        var vaults = vaultIds.Length == 0 ? [] : (await m_session.QueryOver<Vault>()
            .Where(GrantQueryLimits.InChunks(Projections.Property<Vault>(v => v.Id), vaultIds))
            .ListAsync(ct)).ToDictionary(v => v.Id);
        var snapshot = await ForVaultsAsync(actor, vaults.Values.ToArray(), ct);
        var now = DateTime.UtcNow;
        foreach (var secret in secrets)
        {
            result[secret.Id] = snapshot.AuthorizeSecret(vaults[secret.VaultId], secret, required, now);
        }
        return result;
    }

    /// <summary>
    /// Several accounts over several vaults in a constant number of queries (current group memberships, space Admin grants,
    /// vault and secret grants), for decisions about other accounts than the request actor: the collaboration guard decides
    /// the author and every peer of a document at once (WP4 V6). Each snapshot holds what <see cref="ForVaultsAsync"/>
    /// loads for that account alone.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, VaultGrantSnapshot>> ForAccountsAsync(IReadOnlyCollection<UserAccount> actors,
        IReadOnlyCollection<Vault> vaults, CancellationToken ct)
    {
        var result = actors.DistinctBy(a => a.Id).ToDictionary(a => a.Id, VaultGrantSnapshot.Empty);
        var loaded = actors.Where(a => a.IsActive && !a.IsWikiAdmin).Select(a => a.Id).Distinct().ToArray();
        if (loaded.Length == 0 || vaults.Count == 0)
        {
            return result;
        }
        var memberships = await m_session.QueryOver<GroupMembership>()
            .Where(m => m.ActiveTo == null)
            .And(GrantQueryLimits.InChunks(Projections.Property<GroupMembership>(m => m.UserId), loaded))
            .ListAsync(ct);
        var groups = memberships.Select(m => m.GroupId).Distinct().ToArray();
        var admin = await m_session.QueryOver<SpaceGrant>()
            .Where(g => g.ActiveTo == null && g.Role == (int)SpaceRole.Admin)
            .And(GrantQueryLimits.CurrentlyValid())
            .And(OfAccounts(loaded, groups))
            .And(GrantQueryLimits.InChunks(Projections.Property<SpaceGrant>(g => g.SpaceId), vaults.Select(v => v.SpaceId).Distinct().ToArray()))
            .ListAsync(ct);
        var currentSecrets = QueryOver.Of<VaultSecret>().Where(s => s.RemovedAt == null).Select(s => s.Id);
        var grants = await m_session.QueryOver<ResourceGrant>()
            .Where(g => g.ActiveTo == null && g.VaultId != null)
            .And(Restrictions.Or(Restrictions.IsNull(Projections.Property<ResourceGrant>(g => g.SecretId)),
                Subqueries.PropertyIn("SecretId", currentSecrets.DetachedCriteria)))
            .And(GrantQueryLimits.CurrentlyValid())
            .And(OfAccounts(loaded, groups))
            .And(GrantQueryLimits.InChunks(Projections.Property<ResourceGrant>(g => g.VaultId), vaults.Select(v => v.Id).Distinct().ToArray()))
            .ListAsync(ct);
        foreach (var id in loaded)
        {
            var own = memberships.Where(m => m.UserId == id).Select(m => m.GroupId).ToHashSet();
            bool Reaches(Guid? userId, Guid? groupId) => userId == id || (groupId is { } group && own.Contains(group));
            result[id] = new VaultGrantSnapshot(result[id].Actor, admin.Where(g => Reaches(g.UserId, g.GroupId)).ToArray(),
                grants.Where(g => Reaches(g.UserId, g.GroupId)).ToArray(),
                memberships.Where(m => m.UserId == id).ToDictionary(m => m.GroupId, m => m.VerifiedUntil));
        }
        return result;
    }

    /// <summary>Grants of any of the accounts directly or through any of their current groups.</summary>
    private static ICriterion OfAccounts(IList<Guid> users, IList<Guid> groups)
    {
        var direct = GrantQueryLimits.InChunks(Projections.Property("UserId"), users);
        return groups.Count == 0 ? direct : Restrictions.Or(direct, GrantQueryLimits.InChunks(Projections.Property("GroupId"), groups));
    }

    private NHibernate.IQueryOver<SpaceGrant, SpaceGrant> AdminGrants(UserAccount actor)
    {
        return m_session.QueryOver<SpaceGrant>()
            .Where(g => g.ActiveTo == null && g.Role == (int)SpaceRole.Admin)
            .And(GrantQueryLimits.CurrentlyValid())
            .And(GrantQueryLimits.OfAccount(actor.Id));
    }

    private NHibernate.IQueryOver<ResourceGrant, ResourceGrant> VaultGrants(UserAccount actor)
    {
        var currentSecrets = QueryOver.Of<VaultSecret>().Where(s => s.RemovedAt == null).Select(s => s.Id);
        return m_session.QueryOver<ResourceGrant>()
            .Where(g => g.ActiveTo == null && g.VaultId != null)
            .And(Restrictions.Or(Restrictions.IsNull(Projections.Property<ResourceGrant>(g => g.SecretId)),
                Subqueries.PropertyIn("SecretId", currentSecrets.DetachedCriteria)))
            .And(GrantQueryLimits.CurrentlyValid())
            .And(GrantQueryLimits.OfAccount(actor.Id));
    }
}
