using System.Collections.Concurrent;
using Atlas.Server.Features.Vaults.References;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Domain;

/// <summary>
/// The password references of a live collaborative document (WP4 V6, spec 1.1 §6 "Hranice kolaborace"). The shared Yjs state
/// is not filtered per peer, so only accounts that may Discover every reference in it may join, and a reference is admitted only
/// when the author and every current peer may Discover it (SecretReferenceNodeGuard). The references of a generation are those
/// the guard admitted (table CollaborationSecretReference; a reference deleted from the state stays until the next reset, which
/// only ever restricts) plus those of the base revision the state was seeded from.
/// </summary>
public sealed class CollaborationSecretReferences
{
    private readonly ISession m_session;
    private readonly SecretReferenceAccess m_access;
    private readonly RevisionSecretReferences m_revisions;

    public CollaborationSecretReferences(ISession session, SecretReferenceAccess access, RevisionSecretReferences revisions)
    {
        m_session = session;
        m_access = access;
        m_revisions = revisions;
    }

    public async Task<IReadOnlySet<Guid>> OfAsync(Guid pageId, Guid generation, Guid baseRevisionId, CancellationToken ct)
    {
        var admitted = await m_session.CreateSQLQuery("""
            SELECT "SecretId" FROM "CollaborationSecretReference" WHERE "PageId" = :page AND "Generation" = :generation
            """).SetParameter("page", pageId).SetParameter("generation", generation).ListAsync<Guid>(ct);
        var seeded = await m_revisions.OfAsync(m_session, baseRevisionId, ct);
        return admitted.Count == 0 ? seeded : admitted.Concat(seeded).ToHashSet();
    }

    /// <summary>Whether the account may receive the live state: no live reference in it is hidden from the account (dead
    /// references, whose secret is gone, keep nobody out).</summary>
    public async Task<bool> CanJoinAsync(UserAccount account, Guid pageId, Guid generation, Guid baseRevisionId, CancellationToken ct)
    {
        return (await m_access.HiddenAsync(account, await OfAsync(pageId, generation, baseRevisionId, ct), ct)).Count == 0;
    }

    /// <summary>Records admitted references of the generation; nothing for a generation that is no longer current.</summary>
    public async Task RecordAsync(Guid pageId, Guid generation, IEnumerable<Guid> secretIds, CancellationToken ct)
    {
        foreach (var secretId in secretIds)
        {
            await m_session.CreateSQLQuery("""
                INSERT INTO "CollaborationSecretReference"("PageId", "Generation", "SecretId")
                SELECT c."PageId", c."Generation", :secret FROM "PageCollaboration" c WHERE c."PageId" = :page AND c."Generation" = :generation
                ON CONFLICT DO NOTHING
                """).SetParameter("page", pageId).SetParameter("generation", generation).SetParameter("secret", secretId).ExecuteUpdateAsync(ct);
        }
    }

    /// <summary>A reset starts the live state again from a revision: references admitted before it no longer apply.</summary>
    public static Task ClearAsync(ISession session, IEnumerable<Guid> pageIds, CancellationToken ct)
    {
        return session.CreateSQLQuery($"DELETE FROM \"CollaborationSecretReference\" WHERE \"PageId\" = {GuidList.Any(":pages")}")
            .SetParameter("pages", GuidList.Param(pageIds)).ExecuteUpdateAsync(ct);
    }
}

/// <summary>Secret ids referenced by an (immutable) page revision, remembered per process. Content that breaks the reference
/// contract counts as a reference nobody may Discover.</summary>
public sealed class RevisionSecretReferences
{
    private const int Capacity = 10_000;
    private readonly ConcurrentDictionary<Guid, IReadOnlySet<Guid>> m_known = new();

    public async Task<IReadOnlySet<Guid>> OfAsync(ISession session, Guid revisionId, CancellationToken ct)
    {
        if (m_known.TryGetValue(revisionId, out var known))
        {
            return known;
        }
        var json = await session.QueryOver<PageRevision>().Where(r => r.Id == revisionId).Select(r => r.ContentJson).SingleOrDefaultAsync<string>(ct);
        var ids = SecretReferenceAccess.ReferencesOf(json);
        if (m_known.Count >= Capacity)
        {
            m_known.Clear();
        }
        m_known[revisionId] = ids;
        return ids;
    }
}
