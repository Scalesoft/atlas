using System.Runtime.CompilerServices;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Contracts;

namespace Atlas.Server.Features.Collaboration.Domain;

// The relay retains the authenticated object across invocations. Keep the session proof in
// server memory rather than putting a credential/security stamp in its public presence identity.
public sealed class CollaborationSessions
{
    private sealed record Proof(string SecurityStamp, Guid? SessionId);

    private readonly ConditionalWeakTable<CollabUser, Proof> m_proofs = new();

    public CollabUser Register(UserAccount account)
    {
        return Register(new CollaboratorIdentity(account.Id, account.DisplayName, account.SecurityStamp));
    }

    public CollabUser Register(CollaboratorIdentity identity)
    {
        var user = new CollabUser(identity.Id.ToString(), identity.DisplayName);
        m_proofs.Add(user, new Proof(identity.SecurityStamp, identity.SessionId));
        return user;
    }

    public bool Matches(CollabUser user, UserAccount account)
    {
        return Matches(user, account.SecurityStamp);
    }

    public bool TryGetSessionId(CollabUser user, out Guid? sessionId)
    {
        var found = m_proofs.TryGetValue(user, out var proof);
        sessionId = proof?.SessionId;
        return found;
    }

    public bool Matches(CollabUser user, string securityStamp)
    {
        return m_proofs.TryGetValue(user, out var proof) && proof.SecurityStamp == securityStamp;
    }
}
