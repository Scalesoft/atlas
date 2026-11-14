using System.Security.Claims;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Contracts;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Collaboration.Queries;
using Wolverine;

namespace Atlas.Server.Features.Collaboration;

public sealed class AtlasCollaborationAuthenticator : ICollabAuthenticator
{
    private readonly IServiceScopeFactory m_scopes;
    private readonly CollaborationSessions m_sessions;

    public AtlasCollaborationAuthenticator(IServiceScopeFactory scopes, CollaborationSessions sessions)
    {
        m_scopes = scopes;
        m_sessions = sessions;
    }

    public async Task<CollabUser?> AuthenticateAsync(HttpContext handshake, CancellationToken ct)
    {
        // Identity comes from the handshake context, never from the ambient accessor: it is null inside hub invocations.
        if (!Guid.TryParse(handshake.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            || BrowserSessionRevocations.SessionId(handshake.User) is not { } sessionId)
        {
            return null;
        }
        try
        {
            var identity = await InvokeAsync<CollaboratorIdentity>(new AuthenticateCollaborator(id, handshake.User.FindFirstValue("stamp"), sessionId), ct);
            return m_sessions.Register(identity);
        }
        catch (ApiException ex) when (ex.Status == 401)
        {
            return null;
        }
    }

    public async Task<DocAccess> AuthorizeAsync(CollabUser user, string documentId, CancellationToken ct)
    {
        if (!TryDocumentId(documentId, out var pageId, out var generation) || !Guid.TryParse(user.Id, out var userId)
            || !m_sessions.TryGetSessionId(user, out var sessionId))
        {
            return DocAccess.Denied;
        }
        var authorization = await InvokeAsync<CollaboratorAuthorization>(new AuthorizeCollaborator(pageId, generation, userId, sessionId), ct);
        if (!authorization.CanRead || !m_sessions.Matches(user, authorization.SecurityStamp))
        {
            return DocAccess.Denied;
        }
        return authorization.CanWrite ? DocAccess.Write : DocAccess.ReadOnly;
    }

    public static bool TryDocumentId(string value, out Guid pageId, out Guid generation)
    {
        pageId = generation = Guid.Empty;
        var parts = value.Split(':');
        return parts.Length == 3 && parts[0] == "page" && Guid.TryParseExact(parts[1], "N", out pageId)
            && Guid.TryParseExact(parts[2], "N", out generation) && pageId != Guid.Empty && generation != Guid.Empty;
    }

    private async Task<T> InvokeAsync<T>(object message, CancellationToken ct)
    {
        await using var scope = m_scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, ct);
    }
}
