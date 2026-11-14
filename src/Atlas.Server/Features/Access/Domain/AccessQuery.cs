using NHibernate;
using NHibernate.Criterion;

namespace Atlas.Server.Features.Access.Domain;

/// <summary>The search term of the "K čemu" step and the candidate directory, and its case-insensitive substring match.</summary>
public static class AccessQuery
{
    public const int Limit = 20;

    public static string Term(string? query)
    {
        if (query?.Length > 200)
        {
            throw new ApiException(400, "Hledejte nejvýše 200 znaků.");
        }
        return query?.Trim() ?? "";
    }

    /// <summary>strpos over lower(expression), as PageAccessDirectory matches names, so the user's text carries no LIKE wildcards.
    /// The expression names columns through {alias}, for example <c>{alias}."Name"</c>.</summary>
    public static ICriterion Contains(string expression, string term)
    {
        return Expression.Sql($"strpos(lower({expression}), ?) > 0", term.ToLowerInvariant(), NHibernateUtil.String);
    }
}
