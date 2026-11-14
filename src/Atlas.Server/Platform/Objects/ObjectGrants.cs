using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Objects;

/// <summary>
/// A grantee's access to a private object (spec 2026-10-29 §2): the ResourceGrant rows on its ObjectId, decided by the one SQL rule
/// <see cref="AccessSql.ObjectGrantRole"/> (valid now, the account's own or its verified groups', an active account), which the relation
/// nodes and the lists use too. Role 1 reads, 2 writes. The owner and the object's lifecycle are the caller's.
/// </summary>
public static class ObjectGrants
{
    public const int Read = 1;
    public const int Write = 2;

    public static async Task<ObjectAccess> AccessAsync(ISession session, Guid accountId, Guid objectId, CancellationToken ct)
    {
        var role = await session.CreateSQLQuery($"SELECT {AccessSql.ObjectGrantRole(":object", ":account")}")
            .SetParameter("object", objectId).SetParameter("account", accountId)
            .UniqueResultAsync<int?>(ct);
        return role switch
        {
            >= Write => ObjectAccess.Write,
            Read => ObjectAccess.Read,
            _ => ObjectAccess.None
        };
    }
}
