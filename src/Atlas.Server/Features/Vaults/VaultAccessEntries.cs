using NHibernate.Criterion;
using Atlas.Server.Features.Access.Contracts;
using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Features.Vaults.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults;

/// <summary>
/// What the vault and secret access kinds share. A card's levels are VaultGrantSnapshot decisions over VaultGrantSnapshots (a person)
/// or the same rules over the group's own grants: a space Admin manages every vault and secret of the space, everyone else has the
/// highest vault grant and, on a secret, that secret's grants; a secret grant alone shows its vault at Discover. Grants of removed
/// secrets and archived vaults decide nothing and are left out. A resource list mirrors the access tab (VaultAccessDirectory).
/// </summary>
internal static class VaultAccessEntries
{
    public const int EntryLimit = 200;
    public const string SecretsLabel = "Z přístupu k heslům trezoru";

    /// <summary>The principal's current vault and secret grants (valid or not) and its valid space Admin grants in scope.</summary>
    public sealed record Grants(VaultGrantSnapshot? Snapshot, IList<ResourceGrant> Rows, IReadOnlySet<Guid> Valid, IReadOnlyList<SpaceGrant> Admin,
        IReadOnlyDictionary<Guid, string> GroupNames)
    {
        public AccessEntrySource Source(ResourceGrant row, bool viaGroup) => AccessSources.Grant(row.Id, row.GroupId,
            row.GroupId is { } g ? GroupNames.GetValueOrDefault(g) : null, VaultAccessLevels.FromRole(row.Role).Name()!, row.ActiveFrom,
            row.StartAt, Deadline(row.GroupId, row.ExpiresAt), Valid.Contains(row.Id), viaGroup);

        public IEnumerable<AccessEntrySource> AdminSources(Guid spaceId, string spaceName, bool ofUser) => Admin.Where(g => g.SpaceId == spaceId)
            .Select(g => new AccessEntrySource(AccessSources.Automatic, AccessSources.SpaceAdminLabel(spaceName), AccessLevels.Manage,
                ofUser ? g.GroupId : null, g.Id, null, g.StartAt, Deadline(g.GroupId, g.ExpiresAt)));

        private DateTime? Deadline(Guid? groupId, DateTime? expiry) => Snapshot?.GroupDeadlines is { } groups
            ? GrantQueryLimits.Deadline(groupId, expiry, groups) : expiry;
    }

    public static async Task<Grants> LoadAsync(ISession session, VaultGrantSnapshots snapshots, AccessPrincipal who, AccessScope scope,
        CancellationToken ct)
    {
        var (rows, valid) = await AccessGrantRows.ResourceAsync(session, () =>
        {
            var filter = Restrictions.And(AccessGrantRows.Of(who)(), Restrictions.IsNotNull("VaultId"));
            return scope.SpaceId is { } spaceId ? Restrictions.And(filter, Restrictions.Eq("VaultSpaceId", spaceId)) : filter;
        }, ct);
        VaultGrantSnapshot? snapshot = null;
        IReadOnlyList<SpaceGrant> admin;
        if (who.Account is { } account)
        {
            snapshot = await snapshots.ForAccountAsync(account, ct);
            admin = snapshot.AdminGrants.Where(g => scope.Includes(g.SpaceId)).ToArray();
        }
        else
        {
            admin = (await session.QueryOver<SpaceGrant>()
                .Where(g => g.GroupId == who.Id && g.Role == (int)SpaceRole.Admin && g.ActiveTo == null)
                .And(GrantQueryLimits.CurrentlyValid())
                .ListAsync(ct)).Where(g => scope.Includes(g.SpaceId)).ToArray();
        }
        var names = await AccessGrantRows.GroupNamesAsync(session, rows.Select(r => r.GroupId).Concat(admin.Select(g => g.GroupId)), ct);
        return new Grants(snapshot, rows, valid, admin, names);
    }

    /// <summary>Who has access to one vault (Secret null) or secret, for a caller who manages it.</summary>
    public static async Task<ResourceAccess> ResourceAsync(ISession session, VaultAccessTarget target, AccessView view, CancellationToken ct)
    {
        target.Authorize(VaultAccessLevel.Manage).Require();
        var vault = target.Vault!;
        var secret = target.Secret;
        var space = (await session.GetAsync<Space>(vault.SpaceId, ct))!;
        var own = secret is null ? "vault" : "secret";
        var direct = view == AccessView.Direct;
        var groups = (await VaultAccessDirectory.GroupsAsync(session, vault.SpaceId, vault.Id, secret?.Id, ct)).Select(group =>
        {
            var sources = AccessSources.Ordered(group.Sources.Where(s => !direct || s.Kind == own).Select(s => Source(s, space.Name, vault.Name, own, true)));
            return new ResourceAccessEntry(new AccessSubject(AccessPrincipal.Group, group.GroupId, group.Name,
                AccessNotes.Count(group.MemberCount, "člen", "členové", "členů"), group.MemberCount), AccessSources.Level(sources), sources);
        }).Where(e => e.Sources.Count > 0);
        var (people, hasMore) = await VaultAccessDirectory.PeopleAsync(session, vault.SpaceId, vault.Id, secret?.Id, ct, true, EntryLimit);
        var persons = people.Select(person =>
        {
            var sources = AccessSources.Ordered(person.Sources.Where(s => !direct || (s.Kind == own && s.GroupId is null))
                .Select(s => Source(s, space.Name, vault.Name, own, false)));
            return new ResourceAccessEntry(new AccessSubject(AccessPrincipal.User, person.UserId, person.DisplayName, person.Username, null, true),
                AccessSources.Level(sources), sources);
        }).Where(e => e.Sources.Count > 0);
        IReadOnlyList<AutomaticAccess> automatic =
        [
            await AutomaticAccessRows.WikiAdminsAsync(session, "Spravují všechny trezory a hesla", ct, includeHub: false),
            await AutomaticAccessRows.SpaceAdminsAsync(session, space.Id, "Spravují trezory a hesla prostoru", ct, includeHub: false)
        ];
        var resource = secret is null
            ? new AccessResource(own, vault.Id.ToString(), vault.Name, null, space.Id, space.Name)
            : new AccessResource(own, secret.Id.ToString(), secret.Title, vault.Name, space.Id, space.Name);
        return new ResourceAccess(resource, true, automatic, groups.Concat(persons).ToArray(), hasMore);
    }

    /// <summary>Current vaults whose management the actor holds now: all for a WikiAdmin, else those of its Admin spaces and those
    /// with its manage grants, decided by the snapshot.</summary>
    public static async Task<(VaultGrantSnapshot? Snapshot, IReadOnlyList<Vault> Vaults)> ManagedVaultsAsync(ISession session,
        VaultGrantSnapshots snapshots, UserAccount actor, CancellationToken ct)
    {
        var vaults = session.QueryOver<Vault>().Where(v => v.ArchivedAt == null);
        if (actor.IsWikiAdmin)
        {
            return (null, (await vaults.ListAsync(ct)).ToArray());
        }
        var snapshot = await snapshots.ForAccountAsync(actor, ct);
        var now = DateTime.UtcNow;
        var spaces = snapshot.AdminSpaces(now).ToArray();
        var granted = snapshot.Grants.Where(g => g.SecretId is null && g.Role == (int)VaultAccessLevel.Manage).Select(g => g.VaultId!.Value).Distinct().ToArray();
        if (spaces.Length == 0 && granted.Length == 0)
        {
            return (snapshot, []);
        }
        var candidates = await vaults.And(Restrictions.Or(
                GrantQueryLimits.InChunks(Projections.Property<Vault>(v => v.SpaceId), spaces),
                GrantQueryLimits.InChunks(Projections.Property<Vault>(v => v.Id), granted)))
            .ListAsync(ct);
        return (snapshot, candidates.Where(v => snapshot.AuthorizeVault(v, VaultAccessLevel.Manage, now).Allowed).ToArray());
    }

    public static Task<IReadOnlyDictionary<Guid, Space>> SpacesAsync(ISession session, IEnumerable<Vault> vaults, CancellationToken ct)
    {
        return AccessGrantRows.SpacesAsync(session, vaults.Select(v => v.SpaceId), ct);
    }

    /// <summary>A directory source: wikiAdmin and spaceAdmin are automatic; a grant on the listed target is the subject's own
    /// (direct) or its group's; on a secret, a grant on the whole vault reaches it from the vault.</summary>
    private static AccessEntrySource Source(VaultAccessSource source, string spaceName, string vaultName, string own, bool ofGroup)
    {
        return source.Kind switch
        {
            "wikiAdmin" => new AccessEntrySource(AccessSources.Automatic, AccessSources.WikiAdminLabel, AccessLevels.Manage),
            "spaceAdmin" => new AccessEntrySource(AccessSources.Automatic, AccessSources.SpaceAdminLabel(spaceName), AccessLevels.Manage,
                source.GroupId, source.GrantId, null, source.StartAt, source.ExpiresAt, source.Effective),
            _ when source.Kind != own => new AccessEntrySource(AccessSources.Derived,
                ofGroup || source.GroupId is null ? $"Z trezoru {vaultName}" : $"Z trezoru {vaultName} ({source.Name})", source.Level,
                source.GroupId, source.GrantId, source.Version, source.StartAt, source.ExpiresAt, source.Effective),
            _ => new AccessEntrySource(ofGroup || source.GroupId is null ? AccessSources.Direct : AccessSources.Group,
                ofGroup || source.GroupId is null ? AccessSources.DirectLabel : source.Name, source.Level, source.GroupId, source.GrantId,
                source.Version, source.StartAt, source.ExpiresAt, source.Effective)
        };
    }
}
