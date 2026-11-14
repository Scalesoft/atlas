using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.Developer.Queries;

public sealed class FindDeveloperAccountHandler
{
    private readonly ISession m_session;

    public FindDeveloperAccountHandler(ISession session)
    {
        m_session = session;
    }

    public async Task<UserAccount> Handle(FindDeveloperAccount query, CancellationToken ct)
    {
        if (await m_session.GetAsync<Workspace>(1, ct) is null)
        {
            throw new ApiException(409, "Nejprve dokončete úvodní nastavení.", "setup_required");
        }
        // The account's age is its first revision (the history row is written beside it on insert); the id only breaks ties, so the
        // choice stays the same from one sign-in to the next.
        var id = await m_session.CreateSQLQuery("""
                SELECT u."Id" FROM "UserAccount" u
                WHERE u."IsActive" AND u."IsWikiAdmin"
                ORDER BY (SELECT min(r."ActiveFrom") FROM "UserAccountRevision" r WHERE r."UserId" = u."Id"), u."Id"
                LIMIT 1
                """)
            .AddScalar("Id", NHibernateUtil.Guid)
            .UniqueResultAsync<Guid?>(ct);
        return id is { } found
            ? await m_session.GetAsync<UserAccount>(found, ct)
            : throw new ApiException(409, "Wiki nemá žádného aktivního správce, za kterého by se šlo přihlásit.", "no_admin");
    }
}
