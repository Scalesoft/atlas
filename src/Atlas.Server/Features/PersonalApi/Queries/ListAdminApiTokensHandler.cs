using NHibernate.Criterion;
using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalApi.Queries;

public sealed class ListAdminApiTokensHandler
{
    private const int Limit = 1000;

    private readonly ISession m_session;
    private readonly IContentAccess m_access;

    public ListAdminApiTokensHandler(ISession session, IContentAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<IReadOnlyList<AdminApiToken>> Handle(ListAdminApiTokens query, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(null, ct);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            throw new ApiException(403, "API tokeny všech uživatelů spravuje správce wiki.");
        }
        var tokens = await m_session.QueryOver<PersonalApiToken>()
            .OrderBy(t => t.ActiveFrom).Desc
            .ThenBy(t => t.Id).Asc
            .Take(Limit)
            .ListAsync(ct);
        if (tokens.Count == 0)
        {
            return [];
        }
        // Batch collection initialization avoids a query per token, as the profile list does.
        await ApiTokenCollections.LoadAsync(m_session, tokens, ct);
        var owners = (await m_session.QueryOver<UserAccount>()
            .Where(GrantQueryLimits.InChunks(Projections.Property<UserAccount>(u => u.Id), tokens.Select(t => t.UserId).Distinct().ToArray()))
            .ListAsync(ct)).ToDictionary(u => u.Id, u => u.DisplayName);
        return tokens.Select(t => new AdminApiToken(t.Id, t.UserId, owners.GetValueOrDefault(t.UserId) ?? "", t.Name,
            ApiScopes.Ordered(t.Scopes), t.ActiveFrom, t.ExpiresAt, null, t.ActiveTo) { Channels = ApiChannels.Ordered(t.Channels) }).ToArray();
    }
}
