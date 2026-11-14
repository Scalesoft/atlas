using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Commands;
using Wolverine;

namespace Atlas.Server.Features.Collaboration;

/// <summary>
/// Atlas decision on the editor's reserved password-reference nodes (WP4 V6), called by the managed Yjs content guard of
/// Atlas.Markdown.Sync under the document gate before an update, snapshot or seed is stored or broadcast. Every change must
/// keep the reference contract (only the plugin's attributes; an inline value or any other attribute is always refused). Every
/// secret id a payload writes must be one the author and every current peer may Discover (V3 evaluator, one batch for all of
/// them); it is then recorded for the generation. A presentation-only rewrite of an admitted node writes no id. A rewrite of an
/// admitted node to another id (the page source, editor 0.6.0) also needs the old id: the author must not replace a reference
/// hidden from it (<see cref="ReservedNodeChange.CurrentSecretId"/>). A snapshot re-sends every admitted node as new; one with
/// admitted attributes (<see cref="ReservedNodeChange.CurrentAttributes"/>) and the same id needs no new decision. Any failure
/// refuses the payload ([INVALID_CONTENT], fatal for the sender). Singleton: each decision runs one message in its own scope
/// and transaction, like <see cref="PostgresCollaborationStore"/>. No log line carries an attribute value or a secret id.
/// </summary>
public sealed class SecretReferenceNodeGuard : IReservedNodeGuard
{
    private readonly IServiceScopeFactory m_scopes;
    private readonly ILogger<SecretReferenceNodeGuard> m_log;

    public SecretReferenceNodeGuard(IServiceScopeFactory scopes, ILogger<SecretReferenceNodeGuard> log)
    {
        m_scopes = scopes;
        m_log = log;
    }

    public async ValueTask<bool> AdmitAsync(ReservedNodeRequest request, CancellationToken ct)
    {
        if (!AtlasCollaborationAuthenticator.TryDocumentId(request.DocumentId, out var pageId, out var generation)
            || !Guid.TryParse(request.AuthorUserId, out var author))
        {
            return Refuse(request, "unknown document or author");
        }
        var peers = new List<Guid>(request.PeerUserIds.Count);
        foreach (var peer in request.PeerUserIds)
        {
            if (!Guid.TryParse(peer, out var id))
            {
                return Refuse(request, "unknown peer");
            }
            peers.Add(id);
        }
        var secrets = new HashSet<Guid>();
        var replaced = new HashSet<Guid>();
        foreach (var change in request.Changes)
        {
            // A new node (also every node of a seed or snapshot) names its id; a rewrite of an existing node may omit it.
            if (!SecretReferenceContent.IsSecretNodeType(change.NodeName)
                || !SecretReferenceContent.TryValidateAttributes(change.Attributes, change.IsNewNode, out var written))
            {
                return Refuse(request, "attributes outside the reference contract");
            }
            // Non-empty current attributes: the server admitted this node before, with that id.
            Guid? current = change.CurrentAttributes.Count > 0 && Guid.TryParse(change.CurrentSecretId, out var admitted) ? admitted : null;
            var next = written ?? current;
            if (next is not { } id || current == next)
            {
                // A presentation rewrite, or a snapshot re-sending an admitted node: nothing new to decide.
                continue;
            }
            if (current is { } previous)
            {
                replaced.Add(previous);
            }
            secrets.Add(id);
        }
        if (secrets.Count == 0 && replaced.Count == 0)
        {
            return true;
        }
        try
        {
            await using var scope = m_scopes.CreateAsyncScope();
            var admission = await scope.ServiceProvider.GetRequiredService<IMessageBus>()
                .InvokeAsync<SecretReferenceAdmission>(new AdmitSecretReferences(pageId, generation, author, peers, [.. secrets]) { ReplacedIds = [.. replaced] }, ct);
            return admission.Admitted || Refuse(request, "a reference the author or a peer may not discover, or a hidden one replaced");
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            m_log.LogWarning("Password reference decision failed for {DocumentId} ({ErrorType}); refused", request.DocumentId, error.GetType().Name);
            return false;
        }
    }

    private bool Refuse(ReservedNodeRequest request, string reason)
    {
        m_log.LogInformation("Refused a password reference change in {DocumentId} from {UserId}: {Reason}", request.DocumentId, request.AuthorUserId, reason);
        return false;
    }
}
