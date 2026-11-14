using System.Text.Json.Nodes;
using NHibernate;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Domain;
using Atlas.Server.Features.Vaults.References;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Projector.Persistence;

/// <summary>
/// Every definition of the space's catalog pages in catalog order (the page marked first, then document order), repeated keys included
/// (<see cref="RequirementResolver.Distinct"/> keeps the first, so a page marked later never shadows a key). The publications of the
/// catalog pages the actor may read come in one statement, with the read rule of <see cref="RequirementIndex.SpaceAsync"/> (an untrashed
/// page with a page role, or any as WikiAdmin), so an unreadable or unpublished page stays out; it names each publication by its revision
/// and a hash of its content (P2-6), and the content itself is fetched only for a page <see cref="ProjectorCatalogCache"/> does not hold.
/// A page with password references is never cached: it is read for this actor, its references removed as PageReader removes them,
/// decided for all pages at once. Every page read is an opening (<see cref="ProjectorOpenings"/>).
/// </summary>
public sealed class ProjectorCatalogReader(ISession session, IContentAccess access, PageListing listing, SecretReferenceAccess references,
    ProjectorCatalogCache cache, ProjectorOpenings openings)
{
    /// <summary>One readable catalog publication; <see cref="Hash"/> is the md5 of its content.</summary>
    private sealed record Publication(Guid PageId, Guid RevisionId, string Hash);

    public async Task<IReadOnlyList<CatalogDefinition>> ReadAsync(Guid spaceId, CancellationToken ct)
    {
        // The account the rights below are decided for, and the 404 of a space it may not list, as the page tree answers.
        var actor = await access.LockActorAsync(spaceId, ct);
        await listing.RequireSpaceAsync(actor, spaceId, ct);
        var publications = await PublicationsAsync(actor, spaceId, ct);
        var definitions = await DefinitionsAsync(actor, publications, ct);
        await openings.RecordAsync(actor.Id, publications.Select(x => (x.PageId, x.RevisionId)), ct);
        return definitions;
    }

    /// <summary>The catalog publications the actor may read, in catalog order.</summary>
    private async Task<Publication[]> PublicationsAsync(UserAccount actor, Guid spaceId, CancellationToken ct)
        => (await session.CreateSQLQuery($"""
                WITH {AccessSql.ActorCte}
                SELECT c."PageId",r."Id" AS "RevisionId",md5(r."ContentJson") AS "Hash"
                FROM "ProjectorCatalogPage" c JOIN "Page" p ON p."Id"=c."PageId" JOIN "PageRevision" r ON r."Id"=p."PublishedRevisionId" CROSS JOIN actor a
                WHERE p."SpaceId"=:space AND NOT p."IsTrashed"
                  AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e WHERE e."UserId"=a."Id" AND e."PageId"=p."Id" AND e."Role">=1))
                ORDER BY c."MarkedAt",c."PageId"
                """)
            .AddScalar("PageId", NHibernateUtil.Guid).AddScalar("RevisionId", NHibernateUtil.Guid).AddScalar("Hash", NHibernateUtil.String)
            .SetParameter("actor", actor.Id).SetParameter("stamp", actor.SecurityStamp).SetParameter("space", spaceId)
            .ListAsync<object[]>(ct)).Select(row => new Publication((Guid)row[0], (Guid)row[1], (string)row[2])).ToArray();

    /// <summary>The definitions of the publications: from the cache, else parsed (and cached when without password references).</summary>
    private async Task<IReadOnlyList<CatalogDefinition>> DefinitionsAsync(UserAccount actor, Publication[] publications, CancellationToken ct)
    {
        var cached = publications.ToDictionary(x => x.RevisionId, x => cache.Find(x.PageId, x.RevisionId, x.Hash));
        var contents = await ContentsAsync(publications.Where(x => cached[x.RevisionId] is null).Select(x => x.RevisionId).ToArray(), ct);
        var secrets = contents.ToDictionary(x => x.Key, x => SecretIds(x.Value));
        // One decision for every reference of every page; the per-page reads below then find it decided.
        var secretIds = secrets.Values.OfType<IReadOnlySet<Guid>>().SelectMany(ids => ids).ToHashSet();
        if (secretIds.Count > 0)
        {
            await references.DiscoverableAsync([actor], secretIds, ct);
        }
        var definitions = new List<CatalogDefinition>();
        foreach (var publication in publications)
        {
            if (cached[publication.RevisionId] is { } hit)
            {
                definitions.AddRange(hit);
                continue;
            }
            var json = contents[publication.RevisionId];
            if (secrets[publication.RevisionId] is { Count: 0 })
            {
                var read = RequirementTree.Definitions(publication.PageId, Parse(json));
                // shortcut: the parsed trees are charged at 6 bytes per character of the page's JSON (catalog blocks measured 4–6).
                cache.Add(publication.PageId, publication.RevisionId, publication.Hash, 6L * json.Length, read);
                definitions.AddRange(read);
            }
            else
            {
                var (readable, _) = await references.ForReaderAsync(actor, json, ct);
                definitions.AddRange(RequirementTree.Definitions(publication.PageId, Parse(readable)));
            }
        }
        return definitions;
    }

    /// <summary>The content of the revisions the cache does not hold, in one statement; none on a warm cache.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> ContentsAsync(Guid[] revisionIds, CancellationToken ct)
    {
        if (revisionIds.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }
        var rows = await session.CreateSQLQuery("""
                SELECT r."Id",r."ContentJson" FROM "PageRevision" r WHERE r."Id"=ANY(CAST(string_to_array(:ids,',') AS uuid[]))
                """)
            .AddScalar("Id", NHibernateUtil.Guid).AddScalar("ContentJson", NHibernateUtil.StringClob)
            .SetParameter("ids", string.Join(",", revisionIds)).ListAsync<object[]>(ct);
        return rows.ToDictionary(row => (Guid)row[0], row => (string)row[1]);
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json, documentOptions: MarkdownDocumentValidator.JsonOptions)!.AsObject();

    /// <summary>The password references of the content; null when they cannot be read, which ForReaderAsync strips without asking anyone.</summary>
    private static IReadOnlySet<Guid>? SecretIds(string json)
    {
        try
        {
            return SecretReferenceContent.Collect(json);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
