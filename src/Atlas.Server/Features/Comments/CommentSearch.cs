using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Comments;

/// <summary>
/// Comments in search (comments spec §4): hits of type <c>comment</c> in their own group, beside the pages' results and never in
/// their ranking, each leading to its object and anchor (<see cref="ContentSearchTarget"/>) under the object's title. Candidates
/// come from the search documents of live comments on live objects in the spaces the account has a role in; each object is then
/// authorized by its type, as reading its comments is.
/// </summary>
// shortcut: only comments on space objects (pages, boards) are searched, as only those have a comment thread in the UI; add the wiki
// and private scopes when another type shows its comments.
public sealed class CommentSearch : IContentSearchParticipant
{
    public const string Type = "comment";
    /// <summary>A secondary group: a few hits, so authorizing and titling their objects stays cheap.</summary>
    public const int MaxHits = 5;

    private readonly ISession m_session;
    private readonly SpaceRoles m_roles;
    private readonly ObjectTypes m_types;

    public CommentSearch(ISession session, SpaceRoles roles, ObjectTypes types)
    {
        m_session = session;
        m_roles = roles;
        m_types = types;
    }

    public async Task<IReadOnlyList<ContentSearchHit>> SearchAsync(UserAccount actor, string query, string normalized, Guid? spaceId, int limit, CancellationToken ct)
    {
        var expression = SearchText.QueryExpression(query).Expression;
        var spaces = await m_roles.IsWikiAdminAsync(actor, ct) ? null : (await m_roles.EffectiveAsync(actor, ct)).Keys.ToArray();
        if (expression.Length == 0 || spaces is { Length: 0 })
        {
            return [];
        }
        var take = Math.Min(limit, MaxHits);
        var rows = await m_session.CreateSQLQuery($"""
                SELECT c."Id" AS id, c."ObjectId" AS object, r."TypeId" AS type, r."SpaceId" AS space, s."Name" AS name, c."Anchor" AS anchor,
                       {SearchSqlText.CommentText("c.\"Body\"")} AS text
                FROM "CommentSearchDocument" d JOIN "Comment" c ON c."Id" = d."CommentId"
                JOIN "ContentResource" r ON r."Id" = c."ObjectId" JOIN "Space" s ON s."Id" = r."SpaceId",
                     to_tsquery('simple', :expression) q
                WHERE d."Vector" @@ q AND c."DeletedAt" IS NULL AND r."TrashedAt" IS NULL AND r."Scope" = 'space'
                  AND (:all OR r."SpaceId" = ANY(CAST(string_to_array(:spaces, ',') AS uuid[])))
                  AND (CAST(:space AS uuid) IS NULL OR r."SpaceId" = CAST(:space AS uuid))
                ORDER BY ts_rank(d."Vector", q) DESC, c."CreatedAt" DESC, c."Id" LIMIT :candidates
                """)
            .AddScalar("id", NHibernateUtil.Guid).AddScalar("object", NHibernateUtil.Guid).AddScalar("type", NHibernateUtil.String)
            .AddScalar("space", NHibernateUtil.Guid).AddScalar("name", NHibernateUtil.String).AddScalar("anchor", NHibernateUtil.String)
            .AddScalar("text", NHibernateUtil.StringClob)
            .SetParameter("expression", expression).SetParameter("all", spaces is null).SetParameter("spaces", string.Join(',', spaces ?? []))
            .SetParameter("space", spaceId?.ToString(), NHibernateUtil.String)
            // Room for hits on objects the space role shows but the object's own access hides (a restricted page).
            .SetParameter("candidates", take * 3)
            .ListAsync<object[]>(ct);

        var titles = new Dictionary<Guid, string?>();
        var hits = new List<ContentSearchHit>();
        foreach (var row in rows)
        {
            var objectId = (Guid)row[1];
            if (!titles.TryGetValue(objectId, out var title))
            {
                titles[objectId] = title = await ReadableTitleAsync(actor, objectId, (string)row[2], ct);
            }
            if (title is not null)
            {
                hits.Add(new ContentSearchHit(Type, (Guid)row[0], (Guid)row[3], (string)row[4], title, SearchText.Snippet((string)row[6], normalized),
                    new ContentSearchTarget(objectId, (string)row[2], (string?)row[5])));
            }
            if (hits.Count == take)
            {
                break;
            }
        }
        return hits;
    }

    /// <summary>The object's title when the account may read it, as its type projects it; null otherwise.</summary>
    private async Task<string?> ReadableTitleAsync(UserAccount actor, Guid objectId, string typeId, CancellationToken ct)
    {
        if (m_types.Find(typeId) is not { } type || await type.AuthorizeAsync(new ObjectActor(actor), objectId, ct) == ObjectAccess.None)
        {
            return null;
        }
        return (await type.ProjectAsync(objectId, ct))?.Title ?? "";
    }
}
