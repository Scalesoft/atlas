using Atlas.Server.Features.PersonalApi.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalApi.Commands;

public sealed class RevokeApiTokenHandler
{
    private readonly ISession m_session;
    private readonly PersonalApiActor m_actor;

    public RevokeApiTokenHandler(ISession session, PersonalApiActor actor)
    {
        m_session = session;
        m_actor = actor;
    }

    public async Task Handle(RevokeApiToken command, CancellationToken ct)
    {
        var actor = await m_actor.LockAsync(ct);
        var token = await m_session.GetAsync<PersonalApiToken>(command.TokenId, ct);
        if (token is null || token.UserId != actor.Id)
        {
            throw new ApiException(404, "Token nebyl nalezen.");
        }
        if (token.ActiveTo is null)
        {
            token.ActiveTo = Temporal.NextBoundary(token.ActiveFrom, DateTime.UtcNow);
        }
    }
}
