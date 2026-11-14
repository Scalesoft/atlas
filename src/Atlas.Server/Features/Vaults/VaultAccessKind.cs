using NHibernate.Criterion;
using Atlas.Server.Features.Access.Contracts;
using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults;

/// <summary>Vaults as an access kind (WP4 V3 levels discover, read, manage) over VaultGrantSnapshot; grants go through
/// VaultGrantHandler, so every change keeps its fresh sign-in, bounded delegation and CAS.</summary>
public sealed class VaultAccessKind : IAccessKind, IAccessGrantWriter
{
    private static readonly AccessKindDescriptor Kind = new("vault", "Trezor", "Trezory", "lock", "space", null,
    [
        new(AccessLevels.Discover, "Název, cesta a pole hesel, ne hodnoty"),
        new(AccessLevels.Read, "Navíc zobrazí a zkopíruje hodnotu"),
        new(AccessLevels.Manage, "Navíc přístupy, ochrana a audit")
    ]);

    private readonly ISession m_session;
    private readonly IContentAccess m_content;
    private readonly VaultAccess m_access;
    private readonly VaultGrantSnapshots m_snapshots;
    private readonly VaultGrantHandler m_grants;

    public VaultAccessKind(ISession session, IContentAccess content, VaultAccess access, VaultGrantSnapshots snapshots, VaultGrantHandler grants)
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
        var adminSpaces = grants.Admin.Select(g => g.SpaceId).Distinct().ToArray();
        var granted = grants.Rows.Select(g => g.VaultId!.Value).Distinct().ToArray();
        if (!wikiAdmin && adminSpaces.Length == 0 && granted.Length == 0)
        {
            return [];
        }
        var query = m_session.QueryOver<Vault>().Where(v => v.ArchivedAt == null);
        if (scope.SpaceId is { } spaceId)
        {
            query = query.And(v => v.SpaceId == spaceId);
        }
        if (!wikiAdmin)
        {
            query = query.And(Restrictions.Or(GrantQueryLimits.InChunks(Projections.Property<Vault>(v => v.SpaceId), adminSpaces),
                GrantQueryLimits.InChunks(Projections.Property<Vault>(v => v.Id), granted)));
        }
        var vaults = await query.Take(AccessGrantRows.Limit).ListAsync(ct);
        if (vaults.Count == 0)
        {
            return [];
        }
        var spaces = await VaultAccessEntries.SpacesAsync(m_session, vaults, ct);
        var ids = vaults.Select(v => v.Id).ToArray();
        var counts = (await m_session.QueryOver<VaultSecret>().Where(s => s.RemovedAt == null)
            .And(GrantQueryLimits.InChunks(Projections.Property<VaultSecret>(s => s.VaultId), ids))
            .SelectList(list => list.SelectGroup(s => s.VaultId).SelectCount(s => s.Id))
            .ListAsync<object[]>(ct)).ToDictionary(row => (Guid)row[0], row => Convert.ToInt32(row[1]));
        var liveSecrets = await LiveSecretsAsync(grants, ct);
        var viewer = !scope.Viewer.IsWikiAdmin && (!who.IsUser || who.Id != scope.Viewer.Id)
            ? await m_snapshots.ForVaultsAsync(scope.Viewer, vaults.ToArray(), ct) : null;
        var now = DateTime.UtcNow;
        return vaults.Where(v => spaces.ContainsKey(v.SpaceId) && (viewer is null || viewer.AuthorizeVault(v, VaultAccessLevel.Manage, now).Allowed)).Select(vault =>
        {
            var space = spaces[vault.SpaceId];
            var sources = grants.AdminSources(vault.SpaceId, space.Name, who.IsUser)
                .Concat(grants.Rows.Where(g => g.VaultId == vault.Id && g.SecretId is null).Select(g => grants.Source(g, who.IsUser && g.UserId is null)))
                .ToList();
            if (wikiAdmin)
            {
                sources.Add(new AccessEntrySource(AccessSources.Automatic, AccessSources.WikiAdminLabel, AccessLevels.Manage, Effective: account!.IsActive));
            }
            var secretGrants = grants.Rows.Where(g => g.VaultId == vault.Id && g.SecretId is { } s && liveSecrets.Contains(s)).ToArray();
            if (secretGrants.Length > 0)
            {
                sources.Add(new AccessEntrySource(AccessSources.Derived, VaultAccessEntries.SecretsLabel, AccessLevels.Discover,
                    Effective: secretGrants.Any(g => grants.Valid.Contains(g.Id))));
            }
            if (sources.Count == 0)
            {
                // Only grants on removed secrets: they reach nothing.
                return null;
            }
            var ordered = AccessSources.Ordered(sources);
            // A person's level is the VaultGrantSnapshot decision every vault check makes; a group's follows the same rules.
            var level = account is null ? AccessSources.Level(ordered)
                : wikiAdmin ? account.IsActive ? AccessLevels.Manage : null
                : grants.Snapshot!.AuthorizeVault(vault, VaultAccessLevel.Discover, now).Level.Name();
            var count = counts.GetValueOrDefault(vault.Id);
            return new AccessEntry(Kind.Id, vault.Id.ToString(), vault.Name, null, space.Id, space.Name, level, ordered,
                AccessNotes.Count(count, "heslo", "hesla", "hesel"));
        }).OfType<AccessEntry>().OrderBy(e => e.SpaceName, StringComparer.CurrentCulture).ThenBy(e => e.Title, StringComparer.CurrentCulture).ToArray();
    }

    public async Task<ResourceAccess?> ForResourceAsync(Guid resourceId, AccessView view, string? query, CancellationToken ct)
    {
        return await VaultAccessEntries.ResourceAsync(m_session, await m_access.ReadVaultAsync(resourceId, false, ct), view, ct);
    }

    public async Task<IReadOnlyList<AccessResource>> SearchAsync(string? query, CancellationToken ct)
    {
        var term = AccessQuery.Term(query).ToLowerInvariant();
        var actor = await m_content.LockActorAsync(null, ct);
        var (_, vaults) = await VaultAccessEntries.ManagedVaultsAsync(m_session, m_snapshots, actor, ct);
        var found = vaults.Where(v => term.Length == 0 || v.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(v => v.Name, StringComparer.CurrentCulture).Take(AccessQuery.Limit).ToArray();
        var spaces = await VaultAccessEntries.SpacesAsync(m_session, found, ct);
        return found.Where(v => spaces.ContainsKey(v.SpaceId))
            .Select(v => new AccessResource(Kind.Id, v.Id.ToString(), v.Name, null, v.SpaceId, spaces[v.SpaceId].Name)).ToArray();
    }

    public Task ApplyAsync(Guid resourceId, AccessGrantChange change, CancellationToken ct)
    {
        return change.Level is null
            ? m_grants.Handle(new RevokeVaultGrant(resourceId, change.UserId, change.GroupId, change.ExpectedVersion), ct)
            : m_grants.Handle(new SetVaultGrant(resourceId, new UpdateVaultGrant(change.UserId, change.GroupId, change.Level, change.ExpectedVersion,
                change.StartAt, change.ExpiresAt)), ct);
    }

    private async Task<IReadOnlySet<Guid>> LiveSecretsAsync(VaultAccessEntries.Grants grants, CancellationToken ct)
    {
        var ids = grants.Rows.Select(g => g.SecretId).OfType<Guid>().Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new HashSet<Guid>();
        }
        return (await m_session.QueryOver<VaultSecret>().Where(s => s.RemovedAt == null)
            .And(GrantQueryLimits.InChunks(Projections.Property<VaultSecret>(s => s.Id), ids))
            .Select(s => s.Id).ListAsync<Guid>(ct)).ToHashSet();
    }
}
