using Atlas.Server.Features.PersonalApi.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalApi.Commands;

/// <summary>Revokes any account's token the way its owner does (RevokeApiTokenHandler): ActiveTo on the next boundary, idempotent for
/// a token already revoked. Like the owner's revoke it writes no audit event of its own.</summary>
public sealed class RevokeUserApiTokenHandler
{
    private readonly ISession m_session;
    private readonly PersonalApiActor m_actor;

    public RevokeUserApiTokenHandler(ISession session, PersonalApiActor actor)
    {
        m_session = session;
        m_actor = actor;
    }

    public async Task Handle(RevokeUserApiToken command, CancellationToken ct)
    {
        var actor = await m_actor.LockAsync(ct);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            throw new ApiException(403, "API tokeny všech uživatelů spravuje správce wiki.");
        }
        var token = await m_session.GetAsync<PersonalApiToken>(command.TokenId, ct) ?? throw new ApiException(404, "Token nebyl nalezen.");
        if (token.ActiveTo is null)
        {
            token.ActiveTo = Temporal.NextBoundary(token.ActiveFrom, DateTime.UtcNow);
        }
    }
}
