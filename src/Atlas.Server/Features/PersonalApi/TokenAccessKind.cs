using Atlas.Server.Features.Access.Contracts;
using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.PersonalApi.Domain;
using Atlas.Server.Features.PersonalApi.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalApi;

/// <summary>A person's own personal API tokens as an access kind: read, or write once its effective scopes include write; read-only here
/// (tokens are issued in the profile). A token counts as PersonalApiCredentials accepts it: not revoked, not expired, issued under the
/// account's current security stamp, on an active account whose API level is not none and leaves the token a scope. The note names the
/// effective scopes and the channels; nothing records a token's last use, so no note says it.</summary>
public sealed class TokenAccessKind : IAccessKind
{
    private const int TokenLimit = 100;

    private static readonly AccessKindDescriptor Kind = new("token", "API token", "API tokeny", "code-xml", null, null,
    [
        new(AccessLevels.Read, "Čte obsah"),
        new(AccessLevels.Write, "Čte a zapisuje obsah")
    ]);

    private readonly ISession m_session;

    public TokenAccessKind(ISession session)
    {
        m_session = session;
    }

    public AccessKindDescriptor Descriptor => Kind;

    public IAccessGrantWriter? Writer => null;

    public async Task<IReadOnlyList<AccessEntry>> ForPrincipalAsync(AccessPrincipal who, AccessScope scope, CancellationToken ct)
    {
        if (who.Account is not { } account || scope.SpaceId is not null)
        {
            return [];
        }
        var now = DateTime.UtcNow;
        var tokens = await m_session.QueryOver<PersonalApiToken>()
            .Where(t => t.UserId == account.Id && t.ActiveTo == null && t.ExpiresAt > now)
            .OrderBy(t => t.ActiveFrom).Desc
            .Take(TokenLimit)
            .ListAsync(ct);
        if (tokens.Count == 0)
        {
            return [];
        }
        // Batch collection initialization avoids a query per token, as the profile list does.
        await ApiTokenCollections.LoadAsync(m_session, tokens, ct);
        var apiLevel = CurrentApiPermission.Level(await CurrentApiPermission.LoadAsync(m_session, account.Id, ct));
        return tokens.Select(token =>
        {
            var effective = ApiLevels.Effective(token.Scopes, apiLevel);
            var level = effective.Contains(ApiScopes.Write) ? AccessLevels.Write : AccessLevels.Read;
            var live = account.IsActive && apiLevel != ApiLevels.None && effective.Length > 0 && token.SecurityStamp == account.SecurityStamp;
            var source = new AccessEntrySource(AccessSources.Direct, "Osobní token", level, null, token.Id, null, token.ActiveFrom, token.ExpiresAt, live);
            return new AccessEntry(Kind.Id, token.Id.ToString(), token.Name, null, null, null, live ? level : null, [source], Note(effective, token));
        }).ToArray();
    }

    /// <summary>"Čte · Zapisuje · REST API, MCP": what the token may do now, and where.</summary>
    private static string Note(string[] effective, PersonalApiToken token)
    {
        var abilities = effective.Select(scope => scope == ApiScopes.Write ? "Zapisuje" : "Čte");
        var channels = string.Join(", ", ApiChannels.Ordered(token.Channels).Select(channel => channel == ApiChannels.Mcp ? "MCP" : "REST API"));
        return string.Join(" · ", abilities.Append(channels).Where(part => part.Length > 0));
    }

    public Task<ResourceAccess?> ForResourceAsync(Guid resourceId, AccessView view, string? query, CancellationToken ct) => Task.FromResult<ResourceAccess?>(null);

    public Task<IReadOnlyList<AccessResource>> SearchAsync(string? query, CancellationToken ct) => Task.FromResult<IReadOnlyList<AccessResource>>([]);
}
