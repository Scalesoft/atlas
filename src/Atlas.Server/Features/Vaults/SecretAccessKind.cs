using NHibernate.Criterion;
using Atlas.Server.Features.Access.Contracts;
using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults;

/// <summary>Secrets as an access kind: a card lists the secrets with grants of the principal (every other secret follows its vault
/// entry); levels are VaultGrantSnapshot decisions. Grants go through VaultGrantHandler.</summary>
public sealed class SecretAccessKind : IAccessKind, IAccessGrantWriter
{
    private static readonly AccessKindDescriptor Kind = new("secret", "Heslo", "Hesla", "key-round", "vault", null,
    [
        new(AccessLevels.Discover, "Název, cesta a pole hesla"),
        new(AccessLevels.Read, "Navíc zobrazí a zkopíruje hodnotu"),
        new(AccessLevels.Manage, "Navíc přístupy a ochrana hesla")
    ]);

    private readonly ISession m_session;
    private readonly IContentAccess m_content;
    private readonly VaultAccess m_access;
    private readonly VaultGrantSnapshots m_snapshots;
    private readonly VaultGrantHandler m_grants;

    public SecretAccessKind(ISession session, IContentAccess content, VaultAccess access, VaultGrantSnapshots snapshots, VaultGrantHandler grants)
    {
        m_session = session;
        m_content = content;
        m_access = access;
        m_snapshots = snapshots;
        m_grants = grants;
    }

    public AccessKindDescriptor Descriptor => Kind;

    public IAccessGrantWriter Writer => this;

    public async Task<IReadOnlyList<AccessEntry>> ForPrincipalAsync(AccessPrincipal who, AccessScope scope, CancellationToken ct)
    {
        var account = who.Account;
        var wikiAdmin = account is { IsWikiAdmin: true };
        var grants = await VaultAccessEntries.LoadAsync(m_session, m_snapshots, who, scope, ct);
        var secretIds = grants.Rows.Select(g => g.SecretId).OfType<Guid>().Distinct().ToArray();
        if (secretIds.Length == 0)
        {
            return [];
        }
        var secrets = await m_session.QueryOver<VaultSecret>().Where(s => s.RemovedAt == null)
            .And(GrantQueryLimits.InChunks(Projections.Property<VaultSecret>(s => s.Id), secretIds))
            .ListAsync(ct);
        var vaults = secrets.Count == 0 ? new Dictionary<Guid, Vault>() : (await m_session.QueryOver<Vault>().Where(v => v.ArchivedAt == null)
            .And(GrantQueryLimits.InChunks(Projections.Property<Vault>(v => v.Id), secrets.Select(s => s.VaultId).Distinct().ToArray()))
            .ListAsync(ct)).ToDictionary(v => v.Id);
        var spaces = await VaultAccessEntries.SpacesAsync(m_session, vaults.Values, ct);
        var viewer = !scope.Viewer.IsWikiAdmin && (!who.IsUser || who.Id != scope.Viewer.Id)
            ? await m_snapshots.ForVaultsAsync(scope.Viewer, vaults.Values.ToArray(), ct) : null;
        var now = DateTime.UtcNow;
        return secrets.Where(s => vaults.TryGetValue(s.VaultId, out var v) && spaces.ContainsKey(v.SpaceId)
            && (viewer is null || viewer.AuthorizeSecret(v, s, VaultAccessLevel.Manage, now).Allowed)).Select(secret =>
        {
            var vault = vaults[secret.VaultId];
            var space = spaces[vault.SpaceId];
            var sources = grants.AdminSources(vault.SpaceId, space.Name, who.IsUser)
                .Concat(grants.Rows.Where(g => g.VaultId == vault.Id && (g.SecretId is null || g.SecretId == secret.Id))
                    .Select(g => g.SecretId is null
                        ? grants.Source(g, who.IsUser && g.UserId is null) with { Via = AccessSources.Derived, Label = $"Z trezoru {vault.Name}" }
                        : grants.Source(g, who.IsUser && g.UserId is null)))
                .ToList();
            if (wikiAdmin)
            {
                sources.Add(new AccessEntrySource(AccessSources.Automatic, AccessSources.WikiAdminLabel, AccessLevels.Manage, Effective: account!.IsActive));
            }
            var ordered = AccessSources.Ordered(sources);
            var level = account is null ? AccessSources.Level(ordered)
                : wikiAdmin ? account.IsActive ? AccessLevels.Manage : null
                : grants.Snapshot!.AuthorizeSecret(vault, secret, VaultAccessLevel.Discover, now).Level.Name();
            return new AccessEntry(Kind.Id, secret.Id.ToString(), secret.Title, vault.Name, space.Id, space.Name, level, ordered);
        }).OrderBy(e => e.SpaceName, StringComparer.CurrentCulture).ThenBy(e => e.ParentTitle, StringComparer.CurrentCulture)
            .ThenBy(e => e.Title, StringComparer.CurrentCulture).ToArray();
    }

    public async Task<ResourceAccess?> ForResourceAsync(Guid resourceId, AccessView view, string? query, CancellationToken ct)
    {
        return await VaultAccessEntries.ResourceAsync(m_session, await m_access.ReadSecretAsync(resourceId, false, ct), view, ct);
    }

    public async Task<IReadOnlyList<AccessResource>> SearchAsync(string? query, CancellationToken ct)
    {
        var term = AccessQuery.Term(query);
        var actor = await m_content.LockActorAsync(null, ct);
        var (snapshot, managed) = await VaultAccessEntries.ManagedVaultsAsync(m_session, m_snapshots, actor, ct);
        var granted = snapshot?.Grants.Where(g => g.SecretId is not null && g.Role == (int)VaultAccessLevel.Manage).Select(g => g.SecretId!.Value)
            .Distinct().ToArray() ?? [];
        var managedIds = managed.Select(v => v.Id).ToArray();
        if (managedIds.Length == 0 && granted.Length == 0)
        {
            return [];
        }
        var secrets = m_session.QueryOver<VaultSecret>().Where(s => s.RemovedAt == null)
            .And(Restrictions.Or(GrantQueryLimits.InChunks(Projections.Property<VaultSecret>(s => s.VaultId), managedIds),
                GrantQueryLimits.InChunks(Projections.Property<VaultSecret>(s => s.Id), granted)));
        if (term.Length > 0)
        {
            secrets = secrets.And(AccessQuery.Contains("{alias}.\"Title\"", term));
        }
        var found = await secrets.OrderBy(s => s.Title).Asc.ThenBy(s => s.Id).Asc.Take(AccessQuery.Limit).ListAsync(ct);
        var vaults = found.Count == 0 ? new Dictionary<Guid, Vault>() : (await m_session.QueryOver<Vault>().Where(v => v.ArchivedAt == null)
            .And(GrantQueryLimits.InChunks(Projections.Property<Vault>(v => v.Id), found.Select(s => s.VaultId).Distinct().ToArray()))
            .ListAsync(ct)).ToDictionary(v => v.Id);
        var spaces = await VaultAccessEntries.SpacesAsync(m_session, vaults.Values, ct);
        var now = DateTime.UtcNow;
        // The snapshot decides again: a secret grant counts only on a current vault.
        return found.Where(s => vaults.TryGetValue(s.VaultId, out var v) && spaces.ContainsKey(v.SpaceId)
                && (snapshot is null || snapshot.AuthorizeSecret(v, s, VaultAccessLevel.Manage, now).Allowed))
            .Select(s => new AccessResource(Kind.Id, s.Id.ToString(), s.Title, vaults[s.VaultId].Name, vaults[s.VaultId].SpaceId,
                spaces[vaults[s.VaultId].SpaceId].Name)).ToArray();
    }

    public Task ApplyAsync(Guid resourceId, AccessGrantChange change, CancellationToken ct)
    {
        return change.Level is null
            ? m_grants.Handle(new RevokeSecretGrant(resourceId, change.UserId, change.GroupId, change.ExpectedVersion), ct)
            : m_grants.Handle(new SetSecretGrant(resourceId, new UpdateVaultGrant(change.UserId, change.GroupId, change.Level, change.ExpectedVersion,
                change.StartAt, change.ExpiresAt)), ct);
    }
}
