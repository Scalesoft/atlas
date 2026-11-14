using Atlas.Server.Features.PersonalApi.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalApi.Queries;

internal static class ApiTokenCollections
{
    /// <summary>Initializes the scope and channel sets of the listed tokens in two queries instead of two per token.</summary>
    public static async Task LoadAsync(ISession session, IList<PersonalApiToken> tokens, CancellationToken ct)
    {
        if (tokens.Count == 0)
        {
            return;
        }
        var ids = tokens.Select(t => t.Id).ToArray();
        await session.CreateQuery("from PersonalApiToken t left join fetch t.Scopes where t.Id in (:ids)").SetParameterList("ids", ids).ListAsync(ct);
        await session.CreateQuery("from PersonalApiToken t left join fetch t.Channels where t.Id in (:ids)").SetParameterList("ids", ids).ListAsync(ct);
    }
}
