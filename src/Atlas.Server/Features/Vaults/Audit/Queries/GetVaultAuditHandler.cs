using System.Globalization;
using NHibernate;
using NHibernate.Criterion;
using NHibernate.Type;
using Atlas.Server.Features.AccessAudit.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Audit.Queries;

public sealed class GetVaultAuditHandler
{
    private static readonly Dictionary<string, string> KindPrefixes = new(StringComparer.Ordinal)
    {
        ["release"] = "secret.",
        ["request"] = "access_request.",
        ["grant"] = "resource_grant."
    };

    private readonly ISession m_session;
    private readonly VaultAccess m_access;

    public GetVaultAuditHandler(ISession session, VaultAccess access)
    {
        m_session = session;
        m_access = access;
    }

    /// <summary>Spravuje includes the audit (owner decision 27. 9.): 404 when the caller cannot Discover the target, 403 below
    /// Spravuje. The events are narrowed by their indexed targets first, then resolved through AccessAuditView.</summary>
    public async Task<VaultAuditList> Handle(GetVaultAudit query, CancellationToken ct)
    {
        if (query.Limit is < 1 or > 100)
        {
            throw new ApiException(400, "Audit lze načítat po 1 až 100 záznamech.");
        }
        string? prefix = null;
        if (query.Kind is { } kind && !KindPrefixes.TryGetValue(kind, out prefix))
        {
            throw new ApiException(400, "Druh události musí být release, request nebo grant.");
        }
        long? before = null;
        if (query.BeforeId is not null)
        {
            if (!long.TryParse(query.BeforeId, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            {
                throw new ApiException(400, "Neplatný kurzor auditu.");
            }
            before = value;
        }
        var target = query.SecretId is { } secretId
            ? await m_access.ReadSecretAsync(secretId, false, ct)
            : await m_access.ReadVaultAsync(query.VaultId!.Value, false, ct);
        target.Authorize(VaultAccessLevel.Manage).Require();
        var vaultId = target.Vault!.Id;
        var secret = target.Secret?.Id;
        var scope = secret is null ? "" : " AND x.\"SecretId\" = ?";
        IType[] types = secret is null ? [NHibernateUtil.Guid, NHibernateUtil.Guid, NHibernateUtil.Guid] : [.. Enumerable.Repeat<IType>(NHibernateUtil.Guid, 6)];
        object[] values = secret is null ? [vaultId, vaultId, vaultId] : [vaultId, secret.Value, vaultId, secret.Value, vaultId, secret.Value];
        var rows = m_session.QueryOver<AccessAuditView>().Where(Expression.Sql($"""
            {"{alias}"}."Id" IN (
                SELECT e."Id" FROM "AccessAuditEvent" e JOIN "ResourceGrant" x ON x."Id" = e."ResourceGrantId" WHERE x."VaultId" = ?{scope}
                UNION ALL
                SELECT e."Id" FROM "AccessAuditEvent" e JOIN "SecretReleaseEvent" x ON x."Id" = e."SecretReleaseEventId" WHERE x."VaultId" = ?{scope}
                UNION ALL
                SELECT e."Id" FROM "AccessAuditEvent" e JOIN "VaultAccessRequest" x ON x."Id" = e."AccessRequestId" WHERE x."VaultId" = ?{scope})
            """, values, types));
        if (before is { } boundary)
        {
            rows = rows.And(x => x.Id < boundary);
        }
        if (query.ActorId is { } actorId)
        {
            rows = rows.And(x => x.ActorId == actorId);
        }
        if (prefix is not null)
        {
            rows = rows.And(Restrictions.Like(nameof(AccessAuditView.Action), prefix, MatchMode.Start));
        }
        if (query.Result is { } result)
        {
            rows = rows.And(x => x.Result == result);
        }
        if (query.From is { } from)
        {
            rows = rows.And(x => x.OccurredAt >= from.UtcDateTime);
        }
        if (query.To is { } to)
        {
            rows = rows.And(x => x.OccurredAt < to.UtcDateTime);
        }
        var page = await rows.OrderBy(x => x.Id).Desc.Take(query.Limit + 1).ListAsync(ct);
        var items = page.Take(query.Limit).Select(x => x.ToDto()).ToArray();
        return new VaultAuditList(items, page.Count > query.Limit, page.Count > query.Limit ? items[^1].Id : null);
    }
}
