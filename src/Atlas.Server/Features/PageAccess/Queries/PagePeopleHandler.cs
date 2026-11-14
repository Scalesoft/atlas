using Atlas.Server.Features.Search.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageAccess.Queries;

/// <summary>An active account as the editor's contacts directory shows it.</summary>
public sealed record PagePerson(Guid Id, string Name, string Username, string? Email);

/// <summary>GET /api/pages/{id}/people?q=&amp;limit=: active accounts by name, username or e-mail, for contacts and @ mentions;
/// <paramref name="Limit"/> (default 20, at most 500) lets the editor's name recognition read the roster once.</summary>
public sealed record SearchPagePeople(Guid PageId, string? Query, int? Limit = null);

/// <summary>GET /api/pages/{id}/people/{userId}: one active account, for refreshing a contact; 404 otherwise.</summary>
public sealed record GetPagePerson(Guid PageId, Guid UserId);

/// <summary>
/// The people directory of the page editor. It lists every active account, not who reaches the page, so it discloses no
/// access; only someone who may edit the page can ask, because only they can insert a contact.
/// </summary>
public sealed class PagePeopleHandler(ISession session, PageReader reader, IContentAccess access)
{
    private const int Limit = 20, MaxLimit = 500;

    public async Task<IReadOnlyList<PagePerson>> Handle(SearchPagePeople query, CancellationToken ct)
    {
        if (query.Query?.Length > 200)
        {
            throw new ApiException(400, "Hledaný text je příliš dlouhý.");
        }
        await RequireEditableAsync(query.PageId, ct);
        // Every typed word in the name, the login or the e-mail, diacritics and case aside („jan kovarik“ finds „Jan Kovařík“).
        var words = SearchText.LookupWords(query.Query);
        var matches = string.Concat(words.Select((_, i) => $"""
             AND (strpos(lower(public.unaccent(u."DisplayName")), :w{i}) > 0 OR strpos(lower(public.unaccent(u."Username")), :w{i}) > 0
                OR strpos(lower(public.unaccent(coalesce(u."Email", ''))), :w{i}) > 0)
            """));
        var sql = session.CreateSQLQuery($"""
                SELECT u."Id", u."DisplayName", u."Username", u."Email" FROM "UserAccount" u
                WHERE u."IsActive"{matches}
                ORDER BY lower(u."DisplayName"), lower(u."Username"), u."Id"
                """);
        for (var i = 0; i < words.Length; i++)
        {
            sql.SetParameter("w" + i, words[i]);
        }
        var rows = await sql.SetMaxResults(Math.Clamp(query.Limit ?? Limit, 1, MaxLimit)).ListAsync<object[]>(ct);
        return rows.Select(row => new PagePerson((Guid)row[0], (string)row[1], (string)row[2], row[3] as string)).ToArray();
    }

    public async Task<PagePerson> Handle(GetPagePerson query, CancellationToken ct)
    {
        await RequireEditableAsync(query.PageId, ct);
        var account = await session.GetAsync<UserAccount>(query.UserId, ct);
        return account is { IsActive: true }
            ? new PagePerson(account.Id, account.DisplayName, account.Username, account.Email)
            : throw new ApiException(404, "Uživatel nebyl nalezen.");
    }

    private Task RequireEditableAsync(Guid pageId, CancellationToken ct)
    {
        return PageAccessGate.RequireAsync(reader, session, access, pageId, AccessOperation.EditContent, ct);
    }
}
