using Atlas.Server.Features.Spaces.Domain;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Queries;

/// <summary>Read projections of the vault registry shared by queries and commands. Values never appear here.</summary>
public static class VaultReads
{
    public const string Unlocked = "unlocked";
    public const string NeedsPassword = "needsPassword";
    public const string Unavailable = "unavailable";
    public const string Error = "error";

    /// <summary>The badge of a vault, from its persisted state (the refresh writes every transition).</summary>
    public static string Status(Vault vault) => vault.State switch
    {
        VaultStates.Locked => NeedsPassword,
        VaultStates.Error => Error,
        _ => vault.LastFailure is null ? Unlocked : Unavailable,
    };

    /// <summary>Current vaults, all of them or those of the given spaces, ordered by space and name, with the actor's level.</summary>
    public static async Task<IReadOnlyList<VaultView>> ListAsync(ISession session, VaultGrantSnapshots grants, UserAccount actor,
        IReadOnlySet<Guid>? spaces, CancellationToken ct)
    {
        if (spaces is { Count: 0 })
        {
            return [];
        }
        var query = session.QueryOver<Vault>().Where(v => v.ArchivedAt == null);
        if (spaces is not null)
        {
            query.WhereRestrictionOn(v => v.SpaceId).IsIn(spaces.ToArray());
        }
        return await ViewsAsync(session, grants, actor, await query.ListAsync(ct), ct);
    }

    public static async Task<VaultView> ViewAsync(ISession session, VaultGrantSnapshots grants, UserAccount actor, Vault vault, CancellationToken ct) =>
        (await ViewsAsync(session, grants, actor, [vault], ct))[0];

    public static VaultSecretView SecretView(VaultSecret secret, VaultAccessLevel level)
    {
        var fields = new List<string>(4);
        if (secret.AvailableFields.HasFlag(VaultSecretFields.UserName))
        {
            fields.Add("userName");
        }
        if (secret.AvailableFields.HasFlag(VaultSecretFields.Password))
        {
            fields.Add("password");
        }
        if (secret.AvailableFields.HasFlag(VaultSecretFields.Url))
        {
            fields.Add("url");
        }
        if (secret.AvailableFields.HasFlag(VaultSecretFields.Notes))
        {
            fields.Add("notes");
        }
        return new VaultSecretView(secret.Id, secret.EntryUuid, secret.Title, secret.GroupPath, fields, secret.FirstSeenAt, secret.RemovedAt,
            level.Name());
    }

    /// <summary>
    /// The registry entries of one vault the actor may see, with its level on each: every current secret it can Discover
    /// (a space Admin, a WikiAdmin or a vault grant reaches all of them, a secret grant only its own), and removed entries only
    /// with vault-wide manage, the registry view of vault managers. Current entries first (by path and title), then removed ones.
    /// </summary>
    public static IReadOnlyList<VaultSecretView> VisibleSecrets(Vault vault, IEnumerable<VaultSecret> secrets, VaultGrantSnapshot grants, DateTime now)
    {
        var managesVault = grants.AuthorizeVault(vault, VaultAccessLevel.Manage, now).Allowed;
        return secrets
            .Select(secret => (Secret: secret, Decision: grants.AuthorizeSecret(vault, secret, VaultAccessLevel.Discover, now)))
            .Where(item => item.Secret.RemovedAt is null ? item.Decision.Allowed : managesVault)
            .OrderBy(item => item.Secret.RemovedAt is not null)
            .ThenBy(item => item.Secret.GroupPath, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Secret.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => SecretView(item.Secret, item.Decision.Level))
            .ToList();
    }

    public static string? Hex(byte[]? value) => value is null ? null : Convert.ToHexStringLower(value);

    private static async Task<IReadOnlyList<VaultView>> ViewsAsync(ISession session, VaultGrantSnapshots grants, UserAccount actor,
        IList<Vault> vaults, CancellationToken ct)
    {
        if (vaults.Count == 0)
        {
            return [];
        }
        var spaceIds = vaults.Select(v => v.SpaceId).Distinct().ToArray();
        var spaces = (await LiveSpace.Query(session).WhereRestrictionOn(s => s.Id).IsIn(spaceIds).ListAsync(ct))
            .ToDictionary(s => s.Id, s => s.Name);
        // The vaults of a trashed space wait in the trash with it, out of every list.
        vaults = vaults.Where(v => spaces.ContainsKey(v.SpaceId)).ToList();
        if (vaults.Count == 0)
        {
            return [];
        }
        var locationIds = vaults.Select(v => v.LocationId).Distinct().ToArray();
        var vaultIds = vaults.Select(v => v.Id).ToArray();
        var locations = (await session.QueryOver<VaultLocation>().WhereRestrictionOn(l => l.Id).IsIn(locationIds).ListAsync(ct))
            .ToDictionary(l => l.Id, l => l.Name);
        var counts = (await session.QueryOver<VaultSecret>()
                .Where(s => s.RemovedAt == null)
                .WhereRestrictionOn(s => s.VaultId).IsIn(vaultIds)
                .SelectList(list => list.SelectGroup(s => s.VaultId).SelectCount(s => s.Id))
                .ListAsync<object[]>(ct))
            .ToDictionary(row => (Guid)row[0], row => Convert.ToInt32(row[1], System.Globalization.CultureInfo.InvariantCulture));
        var access = await grants.ForVaultsAsync(actor, vaults.ToArray(), ct);
        var now = DateTime.UtcNow;
        return vaults
            .Select(v => new VaultView(v.Id, v.SpaceId, spaces.GetValueOrDefault(v.SpaceId, ""), v.Name, v.LocationId,
                locations.GetValueOrDefault(v.LocationId, ""), v.FileName, Status(v), v.LastFailure, v.LastRefreshAt,
                Hex(v.LastSnapshotSha256), counts.GetValueOrDefault(v.Id), v.CreatedAt,
                access.AuthorizeVault(v, VaultAccessLevel.Discover, now).Level.Name()))
            .OrderBy(v => v.SpaceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
