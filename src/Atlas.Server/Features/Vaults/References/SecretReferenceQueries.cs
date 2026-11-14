using NHibernate.Criterion;
using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Features.Vaults.Requests;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.References;

/// <summary>
/// What the editor may show about one secret it references or offers in the picker: registry metadata only (title, KeePass group
/// path, vault, which fields exist) and the caller's level. Never a value and no protection details: the release decides those.
/// Fields use the registry vocabulary (title, path, userName, password, url, notes); title and path always exist.
/// </summary>
public sealed record SecretReferenceView(Guid Id, string Title, string Path, Guid VaultId, string VaultName, Guid SpaceId,
    IReadOnlyList<string> Fields, string Level)
{
    /// <summary>For a caller who only discovers it: their latest request for this secret or its vault waits for a decision.</summary>
    public bool RequestPending { get; init; }
    /// <summary>For a caller who only discovers it: their latest such request was rejected (they may ask again).</summary>
    public bool RequestRefused { get; init; }
}

/// <summary>GET /api/secrets/search?q=&amp;spaceId=: secrets the caller may Discover, optionally of one space's vaults.</summary>
public sealed record SearchSecretReferences(string? Query, Guid? SpaceId);

/// <summary>GET /api/secrets/{id}/reference: 404 unless the caller may Discover the secret.</summary>
public sealed record GetSecretReference(Guid SecretId);

public sealed class SecretReferenceQueriesHandler
{
    public const int Limit = 30;
    private const int Candidates = 200;

    private readonly ISession m_session;
    private readonly ICurrentActor m_actor;
    private readonly VaultGrantSnapshots m_grants;

    public SecretReferenceQueriesHandler(ISession session, ICurrentActor actor, VaultGrantSnapshots grants)
    {
        m_session = session;
        m_actor = actor;
        m_grants = grants;
    }

    /// <summary>Title or group path containing the query (case-insensitive), ordered by title; at most <see cref="Limit"/>.
    /// Vaults are decided over one grant snapshot, secrets one by one on it (a secret grant reaches only its secret).</summary>
    public async Task<IReadOnlyList<SecretReferenceView>> Handle(SearchSecretReferences query, CancellationToken ct)
    {
        var actor = await m_actor.RequireAsync(ct);
        var text = query.Query?.Trim() ?? "";
        if (text.Length > 200)
        {
            throw new ApiException(400, "Hledaný text je příliš dlouhý.");
        }
        var vaultQuery = m_session.QueryOver<Vault>().Where(v => v.ArchivedAt == null);
        if (query.SpaceId is { } spaceId)
        {
            vaultQuery.And(v => v.SpaceId == spaceId);
        }
        var vaults = (await vaultQuery.ListAsync(ct)).ToArray();
        var snapshot = await m_grants.ForVaultsAsync(actor, vaults, ct);
        var now = DateTime.UtcNow;
        var open = vaults.Where(v => snapshot.AuthorizeVault(v, VaultAccessLevel.Discover, now).Allowed).ToDictionary(v => v.Id);
        if (open.Count == 0)
        {
            return [];
        }
        var secrets = m_session.QueryOver<VaultSecret>()
            .Where(s => s.RemovedAt == null)
            .And(GrantQueryLimits.InChunks(Projections.Property<VaultSecret>(s => s.VaultId), open.Keys.ToArray()));
        if (text.Length > 0)
        {
            secrets.And(Restrictions.Or(
                new LikeExpression("Title", Escape(text), MatchMode.Anywhere, '!', true),
                new LikeExpression("GroupPath", Escape(text), MatchMode.Anywhere, '!', true)));
        }
        var found = await secrets.OrderBy(s => s.Title).Asc.ThenBy(s => s.Id).Asc.Take(Candidates).ListAsync(ct);
        return found
            .Select(secret => (Secret: secret, Vault: open[secret.VaultId], Decision: snapshot.AuthorizeSecret(open[secret.VaultId], secret, VaultAccessLevel.Discover, now)))
            .Where(x => x.Decision.Allowed)
            .Take(Limit)
            .Select(x => View(x.Secret, x.Vault, x.Decision.Level))
            .ToArray();
    }

    public async Task<SecretReferenceView> Handle(GetSecretReference query, CancellationToken ct)
    {
        var actor = await m_actor.RequireAsync(ct);
        var secret = await m_session.GetAsync<VaultSecret>(query.SecretId, ct);
        var vault = secret is null ? null : await m_session.GetAsync<Vault>(secret.VaultId, ct);
        if (secret is null || vault is null)
        {
            throw Missing();
        }
        var decision = (await m_grants.ForVaultsAsync(actor, [vault], ct)).AuthorizeSecret(vault, secret, VaultAccessLevel.Discover, DateTime.UtcNow);
        if (!decision.Allowed)
        {
            throw Missing();
        }
        var view = View(secret, vault, decision.Level);
        if (decision.Level >= VaultAccessLevel.Read)
        {
            return view;
        }
        // The editor keeps showing a pending or refused request across reloads (editor 0.7.0 SecretDiscovery).
        var latest = await m_session.QueryOver<VaultAccessRequest>()
            .Where(r => r.RequesterId == actor.Id && r.VaultId == vault.Id)
            .And(Restrictions.Or(Restrictions.Eq(nameof(VaultAccessRequest.SecretId), secret.Id), Restrictions.IsNull(nameof(VaultAccessRequest.SecretId))))
            .OrderBy(r => r.CreatedAt).Desc
            .Select(r => r.State)
            .Take(1)
            .SingleOrDefaultAsync<string?>(ct);
        return view with { RequestPending = latest == VaultAccessRequest.Pending, RequestRefused = latest == VaultAccessRequest.Rejected };
    }

    public static SecretReferenceView View(VaultSecret secret, Vault vault, VaultAccessLevel level)
    {
        var fields = new List<string>(6) { "title", "path" };
        if (secret.AvailableFields.HasFlag(VaultSecretFields.UserName)) fields.Add("userName");
        if (secret.AvailableFields.HasFlag(VaultSecretFields.Password)) fields.Add("password");
        if (secret.AvailableFields.HasFlag(VaultSecretFields.Url)) fields.Add("url");
        if (secret.AvailableFields.HasFlag(VaultSecretFields.Notes)) fields.Add("notes");
        return new SecretReferenceView(secret.Id, secret.Title, secret.GroupPath, vault.Id, vault.Name, vault.SpaceId, fields, level.Name()!);
    }

    private static string Escape(string text) => text.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");

    private static ApiException Missing() => new(404, "Heslo nebylo nalezeno.");
}
