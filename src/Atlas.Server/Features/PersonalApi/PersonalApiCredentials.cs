using System.Data;
using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Domain;
using Atlas.Server.Features.PersonalApi.Queries;

namespace Atlas.Server.Features.PersonalApi;

/// <summary>Bearer credential verification for /api/v1 (and, with the MCP module, /mcp). Not a Wolverine message: it runs inside the
/// authentication handler and needs RepeatableRead, which the transaction middleware does not offer (spec §9).</summary>
public sealed class PersonalApiCredentials
{
    private readonly AtlasDatabase m_database;

    public PersonalApiCredentials(AtlasDatabase database)
    {
        m_database = database;
    }

    /// <param name="channel">The channel the server derived from the request path (ApiChannels); the token must list it.</param>
    public async Task<PersonalApiPrincipal?> AuthenticateAsync(string secret, string channel, CancellationToken ct)
    {
        if (!ApiSecret.IsWellFormed(secret) || !ApiChannels.All.Contains(channel))
        {
            return null;
        }
        // The authentication handler runs inside the request scope, where the initialized factory is the
        // only source of a session; AtlasDatabaseInitializer owns initialization, and both paths answer an
        // unavailable database with the same ApiException(503).
        using var session = m_database.OpenSession();
        // All credential/account/permission reads must describe one authentication snapshot.
        // A disable/re-enable or a level change between statements must never revive the token read earlier.
        using var transaction = session.BeginTransaction(IsolationLevel.RepeatableRead);
        var hash = ApiSecret.Hash(secret);
        var now = DateTime.UtcNow;
        var token = await session.QueryOver<PersonalApiToken>()
            .Where(x => x.TokenHash == hash && x.ActiveTo == null && x.ExpiresAt > now)
            .SingleOrDefaultAsync(ct);
        if (token is null)
        {
            return null;
        }
        var actor = await session.GetAsync<UserAccount>(token.UserId, ct);
        if (actor is null || !actor.IsActive || actor.SecurityStamp != token.SecurityStamp || !token.Channels.Contains(channel))
        {
            return null;
        }
        // The ceiling is read at every authentication, so lowering it applies to the next request of every token.
        var level = CurrentApiPermission.Level(await CurrentApiPermission.LoadAsync(session, actor.Id, ct));
        var effective = ApiLevels.Effective(token.Scopes, level);
        if (level == ApiLevels.None || effective.Length == 0)
        {
            return null;
        }
        // Read the lazy collections before the session closes.
        var principal = new PersonalApiPrincipal(actor.Id, actor.Username, actor.SecurityStamp, token.Id, effective,
            ApiScopes.Ordered(token.Scopes), channel);
        await transaction.CommitAsync(ct);
        return principal;
    }
}
