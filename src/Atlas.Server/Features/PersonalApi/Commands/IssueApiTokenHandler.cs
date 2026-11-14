using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Domain;
using Atlas.Server.Features.PersonalApi.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalApi.Commands;

public sealed class IssueApiTokenHandler
{
    private readonly ISession m_session;
    private readonly PersonalApiActor m_actor;
    private readonly McpModuleState m_mcp;
    private readonly ISecurityChangeGuard m_guard;

    public IssueApiTokenHandler(ISession session, PersonalApiActor actor, McpModuleState mcp, ISecurityChangeGuard guard)
    {
        m_session = session;
        m_guard = guard;
        m_actor = actor;
        m_mcp = mcp;
    }

    public async Task<CreatedApiToken> Handle(IssueApiToken command, CancellationToken ct)
    {
        var request = command.Request;
        var name = Rules.Required(request.Name, "Název tokenu", 100);
        if (request.ExpiresInDays is < 1 or > 365)
        {
            throw new ApiException(400, "Platnost tokenu musí být 1 až 365 dní.");
        }
        if (!ApiScopes.IsValid(request.Scopes))
        {
            throw new ApiException(400, "Zvolte rozsah Čtení, nebo Zápis.");
        }
        // Without channels a client keeps the 1.5 behaviour: a REST token.
        var channels = request.Channels ?? [ApiChannels.Rest];
        if (!ApiChannels.IsValid(channels))
        {
            throw new ApiException(400, "Zvolte alespoň jeden kanál: REST API nebo MCP.");
        }
        var actor = await m_actor.LockAsync(ct);
        var level = CurrentApiPermission.Level(await CurrentApiPermission.LoadAsync(m_session, actor.Id, ct));
        if (level == ApiLevels.None)
        {
            throw new ApiException(403, "Používání osobních API tokenů musí povolit správce wiki.");
        }
        if (request.Scopes.Except(ApiLevels.Scopes(level)).Any())
        {
            throw new ApiException(403, "Takový rozsah vám správce wiki nepovolil.");
        }
        // SEC-A-2: a token lives up to a year, outlives the session that issued it and may publish, so a stolen session must not
        // turn into one without the password; the ten-minute window keeps issuing several cheap.
        await m_guard.RequireAsync(actor, new SecurityChange("api_token.issue", actor.Id), ct);
        var now = Temporal.NextBoundary(DateTime.UnixEpoch, DateTime.UtcNow);
        var live = await m_session.QueryOver<PersonalApiToken>()
            .Where(x => x.UserId == actor.Id && x.ActiveTo == null && x.ExpiresAt > now)
            .RowCountAsync(ct);
        if (live >= 20)
        {
            throw new ApiException(409, "Nejprve odvolejte některý z dvaceti aktivních tokenů.");
        }
        var secret = ApiSecret.Create();
        // The channel mcp may be chosen while the MCP module is off; the token is then listed as unusable until it is enabled.
        var token = new PersonalApiToken
        {
            Id = Guid.NewGuid(),
            UserId = actor.Id,
            Name = name,
            Prefix = secret[..12],
            TokenHash = ApiSecret.Hash(secret),
            SecurityStamp = actor.SecurityStamp,
            ActiveFrom = now,
            ExpiresAt = now.AddDays(request.ExpiresInDays),
            Scopes = new HashSet<string>(request.Scopes, StringComparer.Ordinal),
            Channels = new HashSet<string>(channels, StringComparer.Ordinal),
        };
        await m_session.SaveAsync(token, ct);
        return new CreatedApiToken(ApiTokenView.ToDto(token, level, actor, now, await m_mcp.IsEnabledAsync(ct)), secret);
    }
}
