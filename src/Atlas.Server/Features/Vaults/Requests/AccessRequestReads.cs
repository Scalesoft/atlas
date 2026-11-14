using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Requests;

/// <summary>Access requests as views: one query with the vault, secret and people names, then one grant snapshot of the reader
/// for CanManage (VaultGrantSnapshot, Spravuje on the target now).</summary>
public static class AccessRequestReads
{
    public const int ListLimit = 200;

    /// <summary>Requests matching <paramref name="where"/> (SQL over alias r), newest first, at most <paramref name="limit"/>.</summary>
    public static async Task<IReadOnlyList<AccessRequestView>> ListAsync(ISession session, VaultGrantSnapshots grants, UserAccount reader,
        string where, IReadOnlyDictionary<string, object> parameters, int limit, CancellationToken ct)
    {
        var query = session.CreateSQLQuery($"""
            SELECT r."Id", r."VaultSpaceId", r."VaultId", v."Name" AS "VaultName", r."SecretId", s."Title" AS "SecretTitle", r."Level",
                r."Reason", r."State", r."RequesterId", requester."DisplayName" AS "RequesterName", r."CreatedAt", r."DecidedBy",
                decider."DisplayName" AS "DeciderName", r."DecidedAt", r."DecisionNote", r."GrantId", r."GrantExpiresAt"
            FROM "VaultAccessRequest" r
            JOIN "Vault" v ON v."Id" = r."VaultId"
            LEFT JOIN "VaultSecret" s ON s."Id" = r."SecretId"
            JOIN "UserAccount" requester ON requester."Id" = r."RequesterId"
            LEFT JOIN "UserAccount" decider ON decider."Id" = r."DecidedBy"
            WHERE {where}
            ORDER BY r."CreatedAt" DESC, r."Id"
            LIMIT {limit}
            """)
            .AddScalar("Id", NHibernateUtil.Guid).AddScalar("VaultSpaceId", NHibernateUtil.Guid).AddScalar("VaultId", NHibernateUtil.Guid)
            .AddScalar("VaultName", NHibernateUtil.String).AddScalar("SecretId", NHibernateUtil.Guid).AddScalar("SecretTitle", NHibernateUtil.String)
            .AddScalar("Level", NHibernateUtil.Int32).AddScalar("Reason", NHibernateUtil.String).AddScalar("State", NHibernateUtil.String)
            .AddScalar("RequesterId", NHibernateUtil.Guid).AddScalar("RequesterName", NHibernateUtil.String)
            .AddScalar("CreatedAt", NHibernateUtil.UtcDateTime).AddScalar("DecidedBy", NHibernateUtil.Guid)
            .AddScalar("DeciderName", NHibernateUtil.String).AddScalar("DecidedAt", NHibernateUtil.UtcDateTime)
            .AddScalar("DecisionNote", NHibernateUtil.String).AddScalar("GrantId", NHibernateUtil.Guid)
            .AddScalar("GrantExpiresAt", NHibernateUtil.UtcDateTime);
        foreach (var (name, value) in parameters)
        {
            query.SetParameter(name, value);
        }
        var rows = await query.ListAsync<object[]>(ct);
        if (rows.Count == 0)
        {
            return [];
        }
        var vaultIds = rows.Select(r => (Guid)r[2]).Distinct().ToArray();
        var secretIds = rows.Where(r => r[4] is not null).Select(r => (Guid)r[4]).Distinct().ToArray();
        var vaults = (await session.QueryOver<Vault>().Where(Restrictions.In(Projections.Property<Vault>(v => v.Id), vaultIds)).ListAsync(ct))
            .ToDictionary(v => v.Id);
        var secrets = secretIds.Length == 0 ? new Dictionary<Guid, VaultSecret>() : (await session.QueryOver<VaultSecret>()
            .Where(Restrictions.In(Projections.Property<VaultSecret>(s => s.Id), secretIds)).ListAsync(ct)).ToDictionary(s => s.Id);
        var snapshot = await grants.ForVaultsAsync(reader, vaults.Values.ToArray(), ct);
        var now = DateTime.UtcNow;
        return rows.Select(r =>
        {
            var vault = vaults[(Guid)r[2]];
            var canManage = (r[4] is Guid secretId
                ? snapshot.AuthorizeSecret(vault, secrets[secretId], VaultAccessLevel.Manage, now)
                : snapshot.AuthorizeVault(vault, VaultAccessLevel.Manage, now)).Allowed;
            return new AccessRequestView((Guid)r[0], (Guid)r[1], (Guid)r[2], (string)r[3], (Guid?)r[4], (string?)r[5],
                VaultAccessLevels.FromRole((int)r[6]).Name()!, (string)r[7], (string)r[8], (Guid)r[9], (string)r[10], (DateTime)r[11],
                (Guid?)r[12], (string?)r[13], (DateTime?)r[14], (string?)r[15], (Guid?)r[16], (DateTime?)r[17], canManage);
        }).ToArray();
    }

    public static async Task<AccessRequestView> ReadAsync(ISession session, VaultGrantSnapshots grants, UserAccount reader, Guid requestId,
        CancellationToken ct)
    {
        var views = await ListAsync(session, grants, reader, "r.\"Id\" = :id", new Dictionary<string, object> { ["id"] = requestId }, 1, ct);
        return views[0];
    }
}
