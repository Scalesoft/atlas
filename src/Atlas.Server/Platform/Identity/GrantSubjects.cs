using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Identity;

/// <summary>Whether the user or group a page, vault or secret grant names exists. Plain reads, no lock: the handlers call it after
/// their own locks and authority check, and SetSpaceGrantHandler looks its subject up through AdministrationReads instead.</summary>
public static class GrantSubjects
{
    /// <summary>404 when the user account or the group does not exist. Expects exactly one of the two ids
    /// (<see cref="GrantValidity.RequireOneSubject"/> comes first).</summary>
    public static async Task RequireExistsAsync(ISession session, Guid? userId, Guid? groupId, CancellationToken ct)
    {
        if (userId is { } user)
        {
            if (await session.GetAsync<UserAccount>(user, ct) is null)
            {
                throw new ApiException(404, "Uživatel nebyl nalezen.");
            }
            return;
        }
        // Raw SQL outside the categories of docs/architecture.md (Konvence kódu), carried over unchanged from the page and vault grant
        // handlers: only the existence of the row matters, and the UserGroup entity lives in Features/Administration, so this counts the
        // row instead of loading it. Convert it to QueryOver<UserGroup> if the entity moves where Platform can use it.
        var exists = await session.CreateSQLQuery("""SELECT count(*) FROM "UserGroup" WHERE "Id" = :id""")
            .SetParameter("id", groupId!.Value).UniqueResultAsync<long>(ct);
        if (exists == 0)
        {
            throw new ApiException(404, "Skupina nebyla nalezena.");
        }
    }
}
