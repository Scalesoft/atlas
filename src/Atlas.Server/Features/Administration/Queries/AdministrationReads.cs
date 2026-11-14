using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.Search.Domain;
using NHibernate;
using NHibernate.Criterion;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Queries;

internal static class AdministrationReads
{
    /// <summary>Every administration list is capped at the same page size the ADO.NET slice used.</summary>
    public const int PageLimit = 1000;

    /// <summary>Every typed word in the display name or the username, case and diacritics aside („novakova“ finds „Nováková“), as the
    /// other people lookups compare (SearchText.LookupWords). PostgreSQL's default LIKE escape is the backslash, so a typed %, _ or \
    /// matches itself. Null when nothing is typed.</summary>
    public static ICriterion? Search(string? text)
    {
        var words = SearchText.LookupWords(text);
        if (words.Length == 0) return null;
        var all = Restrictions.Conjunction();
        foreach (var word in words)
        {
            var pattern = "%" + word.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            all.Add(Expression.Sql("""(lower(public.unaccent({alias}."DisplayName")) LIKE ? OR lower(public.unaccent({alias}."Username")) LIKE ?)""",
                [pattern, pattern], [NHibernateUtil.String, NHibernateUtil.String]));
        }
        return all;
    }

    public static async Task<UserSummary> UserAsync(ISession session, Guid id, CancellationToken ct)
    {
        var user = await session.QueryOver<AdministrationUserView>().Where(x => x.Id == id).SingleOrDefaultAsync(ct);
        return user?.ToSummary() ?? throw new ApiException(404, "Uživatel nebyl nalezen.");
    }

    public static async Task<GroupSummary> GroupAsync(ISession session, Guid id, CancellationToken ct)
    {
        var group = await session.QueryOver<AdministrationGroupView>().Where(x => x.Id == id).SingleOrDefaultAsync(ct);
        return group?.ToSummary() ?? throw new ApiException(404, "Skupina nebyla nalezena.");
    }

    /// <summary>Subselect projections live in the identity map: a second query would return the instance loaded before the write,
    /// with its old version. Flush the pending mutation, then drop the map, exactly like the raw slice cleared its session.</summary>
    public static async Task RefreshAsync(ISession session, CancellationToken ct)
    {
        await session.FlushAsync(ct);
        session.Clear();
    }
}
