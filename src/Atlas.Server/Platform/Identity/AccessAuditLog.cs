using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Identity;

/// <summary>
/// Writes the access audit (AccessAuditEvent): one row per audited change, in the transaction of the change, with the actor the caller
/// passes (null: nobody, as an anonymous activation or a synchronization). Database triggers wrote these rows until 3.6 (no logic in the
/// database, migration 2026110941); the pins in tests/AccessAudit keep the output the same, AccessAuditCoverageTests keeps every writer
/// calling here.
/// </summary>
public static class AccessAuditLog
{
    // The target columns; an event names exactly one (CK_AccessAuditEvent_OneTarget) and its action fits it (CK_AccessAuditEvent_ActionTarget).
    public const string AccountRevision = "AccountRevisionId";
    public const string GroupRevision = "GroupRevisionId";
    public const string GroupMember = "GroupMemberId";
    public const string SpaceGrant = "SpaceGrantId";
    public const string ApiPermission = "ApiPermissionId";
    public const string ResourceGrant = "ResourceGrantId";
    public const string ProtectionSetting = "ProtectionSettingId";
    public const string TrustedAddressRevision = "TrustedAddressRevisionId";
    public const string SecurityRevision = "SecurityRevisionId";
    public const string ReauthChallenge = "ReauthChallengeId";
    public const string SecretReleaseEvent = "SecretReleaseEventId";
    public const string AccessRequest = "AccessRequestId";
    public const string HubChange = "HubChangeId";
    public const string ApiActivity = "ApiActivityId";
    public const string McpSetting = "McpSettingId";
    public const string GroupAiPolicy = "GroupAiPolicyId";
    public const string UserAiPolicy = "UserAiPolicyId";
    public const string ObjectPurgeEvent = "ObjectPurgeEventId";
    public const string BackupDestinationChange = "BackupDestinationChangeId";
    public const string OidcSettingsRevision = "OidcSettingsRevisionId";

    /// <summary>One event. Flushes first: the target may still be a pending insert of the session.</summary>
    public static async Task RecordAsync(ISession session, Guid? actorId, string action, string target, object targetId, CancellationToken ct)
    {
        await session.FlushAsync(ct);
        await session.CreateSQLQuery(Insert(target) + " VALUES (:actor, :action, :target)")
            .SetParameter("actor", actorId, NHibernateUtil.Guid)
            .SetParameter("action", action)
            .SetParameter("target", targetId)
            .ExecuteUpdateAsync(ct);
    }

    /// <summary>One event per row, in the order given: for a set write that returned the ids of the rows it changed.</summary>
    public static async Task RecordAsync(ISession session, Guid? actorId, string action, string target, IReadOnlyCollection<Guid> targetIds,
        CancellationToken ct)
    {
        if (targetIds.Count == 0)
        {
            return;
        }
        await session.FlushAsync(ct);
        await session.CreateSQLQuery(Insert(target)
                + " SELECT :actor, :action, t.id FROM unnest(CAST(string_to_array(:ids, ',') AS uuid[])) WITH ORDINALITY AS t(id, n) ORDER BY t.n")
            .SetParameter("actor", actorId, NHibernateUtil.Guid)
            .SetParameter("action", action)
            .SetParameter("ids", GuidList.Param(targetIds))
            .ExecuteUpdateAsync(ct);
    }

    /// <summary>Access intervals opened (<see cref="GroupMember"/>, <see cref="SpaceGrant"/> or <see cref="ResourceGrant"/> rows):
    /// membership.added, grant.added or resource_grant.added.</summary>
    public static Task OpenedAsync(ISession session, string target, IReadOnlyCollection<Guid> ids, Guid? actorId, CancellationToken ct)
        => RecordAsync(session, actorId, Interval(target) + ".added", target, ids, ct);

    /// <summary>Access intervals closed: membership.removed, grant.removed or resource_grant.removed, then the share links whose issuer
    /// lost Share with them close (ShareRevocation).</summary>
    public static async Task ClosedAsync(ISession session, string target, IReadOnlyCollection<Guid> ids, Guid? actorId, CancellationToken ct)
    {
        await RecordAsync(session, actorId, Interval(target) + ".removed", target, ids, ct);
        await (target switch
        {
            GroupMember => ShareRevocation.MembershipsClosedAsync(session, ids, actorId, ct),
            SpaceGrant => ShareRevocation.SpaceGrantsClosedAsync(session, ids, actorId, ct),
            _ => ShareRevocation.ResourceGrantsClosedAsync(session, ids, actorId, ct),
        });
    }

    private static string Interval(string target) => target switch
    {
        GroupMember => "membership",
        SpaceGrant => "grant",
        ResourceGrant => "resource_grant",
        _ => throw new ArgumentException("Not an access interval: " + target, nameof(target)),
    };

    private static string Insert(string target) => $"INSERT INTO \"AccessAuditEvent\"(\"ActorId\",\"Action\",\"{target}\")";
}
