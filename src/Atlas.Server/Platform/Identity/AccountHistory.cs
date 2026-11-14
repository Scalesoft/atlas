using System.Runtime.CompilerServices;
using NHibernate;
using NHibernate.Engine;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Identity;

/// <summary>
/// The history of an account (UserAccountRevision), its access audit and the closing of its share links, written in the transaction of
/// the change with the actor the caller passes. A writer of UserAccount calls <see cref="RecordAsync"/> after changing the entity and
/// before the session flushes it (it compares the entity with the row it read; the revision holds no stamp to compare with). An account
/// update flushed outside RecordAsync would lose its history silently, so IntegrityRules refuses it (<see cref="IsRecording"/>). Was TR_UserAccount_History, TR_UserAccountRevision_Audit
/// and TR_UserAccount_RevokeShares until 3.6 (migration 2026110941); IdentityAuditPinTests keeps the rows as the triggers wrote them,
/// AccessAuditCoverageTests keeps every writer calling here.
/// </summary>
public static class AccountHistory
{
    private static readonly string[] Revoking = [nameof(UserAccount.SecurityStamp), nameof(UserAccount.IsActive), nameof(UserAccount.IsWikiAdmin)];

    public const string FlushedWithoutHistory = "An account change was flushed before AccountHistory.RecordAsync; record it before any flush";

    private static readonly ConditionalWeakTable<ISessionImplementor, HashSet<Guid>> Recording = new();

    /// <summary>Whether the session flushes this account inside <see cref="RecordAsync"/>, which read its change first.</summary>
    internal static bool IsRecording(ISessionImplementor session, Guid accountId) => Recording.TryGetValue(session, out var ids) && ids.Contains(accountId);

    /// <summary>A new account, or a changed one: a revision for any change (a rotated stamp too); account.created or account.updated when
    /// the sign-in name, the name, the activation or WikiAdmin changed; a rotated stamp, an activation or a WikiAdmin change closes the
    /// account's share links. Nothing for an unchanged account. <paramref name="created"/>: the caller inserted the account and flushed
    /// it already (to read a unique violation), so the session cannot tell it is new.</summary>
    public static async Task RecordAsync(ISession session, UserAccount account, Guid? actorId, CancellationToken ct, bool created = false)
    {
        var changed = created ? null : session.ChangedProperties(account);
        if (changed is { Count: 0 })
        {
            return;
        }
        var recording = Recording.GetOrCreateValue(session.GetSessionImplementation());
        recording.Add(account.Id);
        try
        {
            await session.FlushAsync(ct);
        }
        finally
        {
            recording.Remove(account.Id);
        }
        var previous = await session.CreateSQLQuery("""
            UPDATE "UserAccountRevision" SET "ActiveTo" = greatest(clock_timestamp(), "ActiveFrom" + interval '1 microsecond')
            WHERE "UserId" = :id AND "ActiveTo" IS NULL
            RETURNING "ActiveTo", "Username", "DisplayName", "IsActive", "IsWikiAdmin"
            """).SetParameter("id", account.Id).UniqueResultAsync<object[]>(ct);
        var revisionId = Guid.NewGuid();
        await session.CreateSQLQuery("""
            INSERT INTO "UserAccountRevision"("Id","UserId","Username","DisplayName","IsActive","IsWikiAdmin","ChangedBy","ActiveFrom","ActiveTo","Email")
            SELECT :revision, u."Id", u."Username", u."DisplayName", u."IsActive", u."IsWikiAdmin", :actor, COALESCE(:boundary, clock_timestamp()), NULL, u."Email"
            FROM "UserAccount" u WHERE u."Id" = :id
            """)
            .SetParameter("revision", revisionId)
            .SetParameter("id", account.Id)
            .SetParameter("actor", actorId, NHibernateUtil.Guid)
            .SetParameter("boundary", previous?[0], NHibernateUtil.UtcDateTime)
            .ExecuteUpdateAsync(ct);
        // Password rehashes and stamp rotations are not account metadata edits.
        if (previous is null || !Equals(previous[1], account.Username) || !Equals(previous[2], account.DisplayName)
            || !Equals(previous[3], account.IsActive) || !Equals(previous[4], account.IsWikiAdmin))
        {
            await AccessAuditLog.RecordAsync(session, actorId, previous is null ? "account.created" : "account.updated", AccessAuditLog.AccountRevision,
                revisionId, ct);
        }
        if (changed is not null && changed.Overlaps(Revoking))
        {
            await ShareRevocation.IssuerAsync(session, account.Id, actorId, ct);
        }
    }

    /// <summary>A credential event (credential.reset_requested, password_changed, activated or recovered) on the account's current
    /// revision; no password, hash, token or stamp enters the audit. Flushes first: a stamp rotation of the same change is a revision.</summary>
    public static async Task RecordCredentialAsync(ISession session, Guid userId, string action, Guid? actorId, CancellationToken ct)
    {
        if (action is not ("credential.reset_requested" or "credential.password_changed" or "credential.activated" or "credential.recovered"))
        {
            throw new ArgumentException("Invalid credential audit action", nameof(action));
        }
        await session.FlushAsync(ct);
        var revision = await session.CreateSQLQuery("""SELECT "Id" FROM "UserAccountRevision" WHERE "UserId" = :id AND "ActiveTo" IS NULL""")
            .SetParameter("id", userId).UniqueResultAsync<Guid?>(ct) ?? throw new InvalidOperationException("The account has no current revision.");
        await AccessAuditLog.RecordAsync(session, actorId, action, AccessAuditLog.AccountRevision, revision, ct);
    }
}
