using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.Administration.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Commands;

internal static class GroupNames
{
    /// <summary>Uniqueness is case insensitive in the database collation, so both sides go through SQL lower().</summary>
    public static async Task<bool> ExistsAsync(ISession session, string name, Guid? exceptId, CancellationToken ct)
    {
        var query = session.QueryOver<UserGroup>()
            .Where(Restrictions.EqProperty(
                Projections.SqlFunction("lower", NHibernateUtil.String, Projections.Property<UserGroup>(x => x.Name)),
                Projections.SqlFunction("lower", NHibernateUtil.String, Projections.Constant(name))));
        if (exceptId is { } id)
        {
            query = query.And(x => x.Id != id);
        }
        return await query.RowCountAsync(ct) > 0;
    }
}
