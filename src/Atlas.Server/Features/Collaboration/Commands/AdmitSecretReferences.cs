using Atlas.Server.Features.Vaults.References;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Commands;

/// <summary>The decision of SecretReferenceNodeGuard: the author and every peer (the users joined to the document) may Discover
/// every secret id the generation does not hold yet; those ids are then recorded for the generation. <see cref="ReplacedIds"/>
/// are the old ids of rewritten nodes: none may be a live reference hidden from the author.</summary>
public sealed record AdmitSecretReferences(Guid PageId, Guid Generation, Guid AuthorId, IReadOnlyList<Guid> PeerIds, IReadOnlyList<Guid> SecretIds)
{
    public IReadOnlyList<Guid> ReplacedIds { get; init; } = [];
}

public sealed record SecretReferenceAdmission(bool Admitted);

public sealed class AdmitSecretReferencesHandler
{
    private readonly ISession m_session;
    private readonly SecretReferenceAccess m_access;
    private readonly CollaborationSecretReferences m_references;

    public AdmitSecretReferencesHandler(ISession session, SecretReferenceAccess access, CollaborationSecretReferences references)
    {
        m_session = session;
        m_access = access;
        m_references = references;
    }

    public async Task<SecretReferenceAdmission> Handle(AdmitSecretReferences command, CancellationToken ct)
    {
        var ids = command.PeerIds.Append(command.AuthorId).Distinct().ToArray();
        var accounts = await m_session.QueryOver<UserAccount>().WhereRestrictionOn(a => a.Id).IsIn(ids).ListAsync(ct);
        if (accounts.Count != ids.Length || accounts.Any(a => !a.IsActive))
        {
            return new SecretReferenceAdmission(false);
        }
        var baseRevision = await m_session.QueryOver<PageCollaboration>()
            .Where(x => x.PageId == command.PageId && x.Generation == command.Generation)
            .Select(x => x.BaseRevisionId)
            .SingleOrDefaultAsync<Guid>(ct);
        if (baseRevision == Guid.Empty)
        {
            return new SecretReferenceAdmission(false);
        }
        // References the generation already holds passed this check when they entered (or were saved by someone who could use
        // them); every current peer passed the join check for them. Snapshots repeat them all, even after their secret is gone.
        var author = accounts.Single(a => a.Id == command.AuthorId);
        // The author must not overwrite what it may not see (a dead reference hides nothing), like a save over hidden references.
        if (command.ReplacedIds.Count > 0 && (await m_access.HiddenAsync(author, command.ReplacedIds.ToHashSet(), ct)).Count > 0)
        {
            return new SecretReferenceAdmission(false);
        }
        var known = await m_references.OfAsync(command.PageId, command.Generation, baseRevision, ct);
        var added = command.SecretIds.Where(id => !known.Contains(id)).ToArray();
        if (added.Length == 0)
        {
            return new SecretReferenceAdmission(true);
        }
        var decisions = await m_access.DiscoverableAsync(accounts.ToArray(), added, ct);
        if (decisions.Values.Any(visible => added.Any(id => !visible.Contains(id))))
        {
            return new SecretReferenceAdmission(false);
        }
        await m_references.RecordAsync(command.PageId, command.Generation, added, ct);
        return new SecretReferenceAdmission(true);
    }
}
