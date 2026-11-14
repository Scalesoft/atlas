using NHibernate;
using NHibernate.Criterion;

namespace Atlas.Server.Platform.Persistence;

/// <summary>A list of ids as one text parameter, not one per id: PostgreSQL takes at most 65,535 parameters per statement, and the
/// statement keeps one shape whatever the number of ids.</summary>
public static class GuidList
{
    /// <summary>The value of the parameter.</summary>
    public static string Param(IEnumerable<Guid> ids) => string.Join(',', ids);

    /// <summary>The parameter read as uuid[], for <c>"Id" = {Any(":ids")}</c>.</summary>
    public static string Any(string parameter) => $"ANY(CAST(string_to_array({parameter}, ',') AS uuid[]))";

    /// <summary>The column of the criteria's entity is one of the ids.</summary>
    public static ICriterion In(string column, IEnumerable<Guid> ids) =>
        Expression.Sql($"{{alias}}.\"{column}\" = {Any("?")}", Param(ids), NHibernateUtil.String);
}
