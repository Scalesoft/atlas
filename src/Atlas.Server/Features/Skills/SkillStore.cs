using System.Data.Common;
using System.Text.Json;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Platform.Persistence;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Skills;

/// <summary>
/// The SQL of people's topics (docs/skills-design.md §2–§5). Page access is <see cref="PageTagStore.AccessiblePages"/> (the reader's pages,
/// on MCP not of a space switched out of AI); contacts are filtered by the caller (ContactDirectory). The anonymous ratings never leave this
/// class one by one: a summary counts them and averages them, only a wiki admin gets the raters.
/// </summary>
public sealed class SkillStore(ISession session)
{
    /// <summary>A name folded for duplicates: lower case, no diacritics, nothing but letters, digits, # and + („C Sharp“ = „c-sharp“, „C#“ stays).</summary>
    public static string Fold(string expression) => $"regexp_replace(lower(public.unaccent({expression})), '[^a-z0-9#+]+', '', 'g')";

    /// <summary>The account whose own contact each of :contacts is (its account contact: directoryId, live, not private, as
    /// <see cref="AccountContacts.OwnContact"/> and <see cref="OwnContactAsync"/>), null for a contact of nobody. The link is only an admin's to
    /// move (ContactObjectType.RefuseIdentityMoveAsync): nobody makes a colleague's contact their own by editing it.</summary>
    private const string Owners = """
        owner AS (
          SELECT c."Id" AS contact, a."Id" AS account FROM "ContentResource" c
          LEFT JOIN "UserAccount" a ON CAST(a."Id" AS text) = lower(c."Data"->>'directoryId') AND c."TrashedAt" IS NULL AND c."Scope" <> 'private'
          WHERE c."Id" = ANY(:contacts)
        )
        """;

    /// <summary>A rating of rating row r that others may count: not private, of a live contact that is not somebody's private one (a topic
    /// rated on a private contact or in the trash names nothing to anybody but its rater).</summary>
    private const string Counted = """
        r."Visibility" <> 'private' AND EXISTS (SELECT 1 FROM "ContentResource" rc WHERE rc."Id" = r."ContactId" AND rc."TrashedAt" IS NULL AND rc."Scope" <> 'private')
        """;

    /// <summary>How many people know each topic, counting only <see cref="Counted"/> ratings.</summary>
    private const string People = $$"""
        people AS (SELECT r."TagId", count(DISTINCT r."ContactId") AS n FROM "SkillRating" r WHERE {{Counted}} GROUP BY r."TagId")
        """;

    /// <summary>Tags the reader may know of: on a readable page, or rated in a way the reader may see.</summary>
    private const string VisibleTags = $$"""
        visible AS (
          SELECT pt."TagId" FROM "PageTag" pt JOIN accessible p ON p."Id" = pt."PageId" WHERE pt."ActiveTo" IS NULL
          UNION SELECT r."TagId" FROM "SkillRating" r WHERE r."RaterId" = :actor OR {{Counted}}
        ), {{People}}
        """;

    /// <summary><see cref="Anonymous"/>: how many of <see cref="Peers"/> are shared (not signed) by others than the reader, the ones the
    /// threshold protects; <see cref="Signed"/> and <see cref="SignedAverage"/> the signed ones; <see cref="Band"/> and <see cref="BandAverage"/>
    /// the oldest anonymous ratings up to the band their count is in (<see cref="Bands"/>), the only ones a reader's number is made of
    /// (SkillsHandler.View). <see cref="Peers"/> and <see cref="PeerAverage"/> are exact, for a wiki admin in the app only.</summary>
    public sealed record Summary(Guid ContactId, Guid TagId, string Name, int? Self, string? SelfNote, int Peers, double? PeerAverage, int? Mine, string? MyVisibility,
        string? MyNote, IReadOnlyList<SkillRater> Raters, string? Group = null, int Anonymous = 0, int Signed = 0, double? SignedAverage = null, int Band = 0,
        double? BandAverage = null);

    /// <summary>The bands of anonymous ratings (3–4, 5–9, 10–19, 20–49, 50+): a reader's number takes the oldest ratings up to the band's start,
    /// so one more anonymous rating inside a band changes nothing anybody sees, and crossing into the next adds at least two at once.</summary>
    public static readonly int[] Bands = [3, 5, 10, 20, 50];

    private static readonly string BandStart = "CASE " + string.Join(" ", Bands.Reverse().Select(start => $"WHEN anon_n >= {start} THEN {start}")) + " ELSE anon_n END";

    /// <summary>The ratings of :contacts (in :tags, or all when null) as <paramref name="actor"/> may see them, one row per contact and topic.</summary>
    public Task<List<Summary>> SummariesAsync(UserAccount actor, bool admin, bool aiScopeOnly, Guid[] contacts, Guid[]? tags, CancellationToken ct)
        => session.ReadRowsAsync($$"""
            {{PageTagStore.AccessiblePages}}, {{VisibleTags}}, {{Owners}}, joined AS (
              SELECT r.*, COALESCE(r."RaterId" = o.account, false) AS self, u."DisplayName" AS rater,
                NOT COALESCE(r."RaterId" = o.account, false) AND r."Visibility" = 'shared' AS anon
              FROM "SkillRating" r JOIN owner o ON o.contact = r."ContactId" JOIN "UserAccount" u ON u."Id" = r."RaterId"
              WHERE :alltags OR r."TagId" = ANY(:tags)
            ), counted AS (
              -- Each anonymous rating's place among the person's in the topic, oldest first (the id is time-ordered), and how many there are.
              SELECT j.*, row_number() OVER (PARTITION BY j."ContactId", j."TagId", j.anon ORDER BY j."Id") AS place,
                count(*) FILTER (WHERE j.anon) OVER (PARTITION BY j."ContactId", j."TagId") AS anon_n
              FROM joined j
            ), rated AS (
              SELECT c.*, c.anon AND c.place <= {{BandStart}} AS banded FROM counted c
            )
            SELECT r."ContactId", t."Id", t."Name",
              max(r."Level") FILTER (WHERE r.self AND (r."Visibility" <> 'private' OR r."RaterId" = :actor)),
              max(r."Note") FILTER (WHERE r.self AND (r."Visibility" <> 'private' OR r."RaterId" = :actor)),
              count(*) FILTER (WHERE NOT r.self AND r."Visibility" <> 'private'),
              avg(r."Level") FILTER (WHERE NOT r.self AND r."Visibility" <> 'private')::float8,
              max(r."Level") FILTER (WHERE r."RaterId" = :actor),
              max(r."Visibility") FILTER (WHERE r."RaterId" = :actor),
              max(r."Note") FILTER (WHERE r."RaterId" = :actor),
              COALESCE(jsonb_agg(jsonb_build_object('name', r.rater, 'level', r."Level", 'visibility', r."Visibility", 'note', r."Note") ORDER BY r."Level" DESC, r.rater)
                FILTER (WHERE NOT r.self AND r."RaterId" <> :actor AND (r."Visibility" = 'signed' OR :admin AND r."Visibility" = 'shared')), '[]')::text,
              (SELECT min(g."Name") FROM "TagRelation" k JOIN "Tag" g ON g."Id" = k."ParentId" WHERE k."TagId" = t."Id" AND k."Kind" = 'part_of'
                AND g."Id" IN (SELECT "TagId" FROM visible)),
              count(*) FILTER (WHERE NOT r.self AND r."Visibility" = 'shared' AND r."RaterId" <> :actor),
              count(*) FILTER (WHERE NOT r.self AND r."Visibility" = 'signed'),
              avg(r."Level") FILTER (WHERE NOT r.self AND r."Visibility" = 'signed')::float8,
              count(*) FILTER (WHERE r.banded),
              avg(r."Level") FILTER (WHERE r.banded)::float8
            FROM rated r JOIN "Tag" t ON t."Id" = r."TagId"
            GROUP BY r."ContactId", t."Id", t."Name"
            HAVING bool_or(r."Visibility" <> 'private' OR r."RaterId" = :actor)
            """, new Dictionary<string, object>
            {
                ["contacts"] = contacts, ["tags"] = tags ?? [], ["alltags"] = tags is null, ["actor"] = actor.Id, ["admin"] = admin,
                ["stamp"] = actor.SecurityStamp, ["ai"] = aiScopeOnly
            }, row => new Summary(row.GetGuid(0), row.GetGuid(1), row.GetString(2), Int(row, 3), Text(row, 4), (int)row.GetInt64(5),
                row.IsDBNull(6) ? null : row.GetDouble(6), Int(row, 7), Text(row, 8), Text(row, 9),
                JsonSerializer.Deserialize<SkillRater[]>(row.GetString(10), JsonSerializerOptions.Web) ?? [], Text(row, 11), (int)row.GetInt64(12), (int)row.GetInt64(13), row.IsDBNull(14) ? null : row.GetDouble(14),
                (int)row.GetInt64(15), row.IsDBNull(16) ? null : row.GetDouble(16)), ct);

    public sealed record Evidence(Guid ContactId, Guid TagId, Guid PageId, string Title, string SpaceName, bool InContext);

    /// <summary>The readable pages with a current tag of :tags (all when null) that each of :contacts wrote (a revision by its account) or
    /// is mentioned on; <see cref="Evidence.InContext"/> when the page is in a space named like <paramref name="context"/> or carries a tag of
    /// that name. Newest first, at most 500 rows.</summary>
    // shortcut: scans the reader's tagged pages per call (EXISTS on PageRevision by author); a derived page–author table once wikis with
    // tens of thousands of tagged pages show it in the request budget.
    public Task<List<Evidence>> EvidenceAsync(UserAccount actor, bool aiScopeOnly, Guid[] contacts, Guid[]? tags, string context, CancellationToken ct)
        => session.ReadRowsAsync($$"""
            {{PageTagStore.AccessiblePages}}, {{Owners}}
            SELECT o.contact, pt."TagId", p."Id", COALESCE(p."PublishedTitle", p."WorkingTitle"), p."SpaceName",
              :context <> '' AND ({{Fold("p.\"SpaceName\"")}} LIKE '%' || {{Fold(":context")}} || '%'
                OR EXISTS (SELECT 1 FROM "PageTag" c JOIN "Tag" ct ON ct."Id" = c."TagId" WHERE c."PageId" = p."Id" AND c."ActiveTo" IS NULL
                  AND {{Fold("ct.\"Name\"")}} = {{Fold(":context")}}))
            FROM accessible p
            JOIN "PageTag" pt ON pt."PageId" = p."Id" AND pt."ActiveTo" IS NULL AND (:alltags OR pt."TagId" = ANY(:tags))
            JOIN owner o ON EXISTS (SELECT 1 FROM "PageRevision" r WHERE r."PageId" = p."Id" AND r."AuthorId" = o.account)
              OR EXISTS (SELECT 1 FROM "PageObjectMention" m WHERE m."SourcePageId" = p."Id" AND m."ObjectId" = o.contact)
            ORDER BY GREATEST(p."PublishedAt", p."WorkingAt") DESC
            LIMIT 500
            """, new Dictionary<string, object>
            {
                ["actor"] = actor.Id, ["stamp"] = actor.SecurityStamp, ["ai"] = aiScopeOnly, ["contacts"] = contacts, ["tags"] = tags ?? [],
                ["alltags"] = tags is null, ["context"] = context
            }, row => new Evidence(row.GetGuid(0), row.GetGuid(1), row.GetGuid(2), row.GetString(3), row.GetString(4), row.GetBoolean(5)), ct);

    /// <summary>The contacts that may know :tags: rated in them (as the reader may see) or writing or mentioned on their readable pages.</summary>
    public Task<List<Guid>> CandidatesAsync(UserAccount actor, bool aiScopeOnly, Guid[] tags, CancellationToken ct)
        => session.ReadRowsAsync($$"""
            {{PageTagStore.AccessiblePages}}, tagged AS (
              SELECT p."Id" FROM accessible p JOIN "PageTag" pt ON pt."PageId" = p."Id" AND pt."ActiveTo" IS NULL AND pt."TagId" = ANY(:tags)
            )
            SELECT r."ContactId" FROM "SkillRating" r WHERE r."TagId" = ANY(:tags) AND (r."Visibility" <> 'private' OR r."RaterId" = :actor)
            UNION
            SELECT c."Id" FROM tagged p JOIN "PageRevision" r ON r."PageId" = p."Id"
              JOIN "ContentResource" c ON c."TypeId" = 'atlas.contact' AND c."TrashedAt" IS NULL AND c."Scope" <> 'private' AND c."Data"->>'directoryId' IS NOT NULL
                AND lower(c."Data"->>'directoryId') = CAST(r."AuthorId" AS text)
            UNION
            SELECT m."ObjectId" FROM tagged p JOIN "PageObjectMention" m ON m."SourcePageId" = p."Id"
            LIMIT 200
            """, new Dictionary<string, object> { ["actor"] = actor.Id, ["stamp"] = actor.SecurityStamp, ["ai"] = aiScopeOnly, ["tags"] = tags },
            row => row.GetGuid(0), ct);

    /// <summary>Visible topics by text: the exact folded name or alias first, then a prefix, a part, a similar spelling (trigram ≥ 0.4).
    /// An empty text gives the topics most people know.</summary>
    public Task<List<TopicRef>> SearchAsync(UserAccount actor, bool aiScopeOnly, string query, Guid? except, int limit, CancellationToken ct)
        => session.ReadRowsAsync($$"""
            {{PageTagStore.AccessiblePages}}, {{VisibleTags}}, names AS (
              SELECT t."Id", t."Name", t."Name" AS matched FROM "Tag" t WHERE t."MergedIntoId" IS NULL
              UNION ALL SELECT t."Id", t."Name", a."Name" FROM "TagAlias" a JOIN "Tag" t ON t."Id" = a."TagId" WHERE t."MergedIntoId" IS NULL
            ), folded AS (
              SELECT n."Id", n."Name", n.matched, {{Fold("n.matched")}} AS f, {{Fold(":query")}} AS q FROM names n
              WHERE n."Id" IN (SELECT "TagId" FROM visible) AND n."Id" <> :except
            ), ranked AS (
              SELECT "Id", "Name", matched, CASE WHEN q = '' THEN 4 WHEN f = q THEN 0 WHEN f LIKE q || '%' THEN 1 WHEN position(q IN f) > 0 THEN 2
                WHEN length(q) >= 3 AND similarity(f, q) >= 0.4 THEN 3 END AS rank, similarity(f, q) AS near FROM folded
            ), best AS (
              SELECT DISTINCT ON ("Id") "Id", "Name", matched, rank, near FROM ranked WHERE rank IS NOT NULL ORDER BY "Id", rank, near DESC
            )
            SELECT b."Id", b."Name", COALESCE(p.n, 0)::int, CASE WHEN b.matched <> b."Name" THEN b.matched ELSE '' END
            FROM best b LEFT JOIN people p ON p."TagId" = b."Id"
            ORDER BY b.rank, COALESCE(p.n, 0) DESC, b.near DESC, lower(b."Name")
            LIMIT :limit
            """, new Dictionary<string, object>
            {
                ["actor"] = actor.Id, ["stamp"] = actor.SecurityStamp, ["ai"] = aiScopeOnly, ["query"] = query, ["except"] = except ?? Guid.Empty, ["limit"] = limit
            }, row => new TopicRef(row.GetGuid(0), row.GetString(1), row.GetInt32(2), row.GetString(3) is { Length: > 0 } matched ? matched : null), ct);

    /// <summary>Whether the reader may know of the tag (<see cref="VisibleTags"/>); a merge or a link of a tag nobody showed them is a 404.</summary>
    public async Task<bool> VisibleAsync(UserAccount actor, bool aiScopeOnly, Guid tagId, CancellationToken ct)
        => (await session.ReadRowsAsync($$"""
            {{PageTagStore.AccessiblePages}}, {{VisibleTags}}
            SELECT EXISTS (SELECT 1 FROM visible v JOIN "Tag" t ON t."Id" = v."TagId" WHERE v."TagId" = :tag AND t."MergedIntoId" IS NULL)
            """, new Dictionary<string, object> { ["actor"] = actor.Id, ["stamp"] = actor.SecurityStamp, ["ai"] = aiScopeOnly, ["tag"] = tagId },
            row => row.GetBoolean(0), ct)).Single();

    /// <summary>The topic with its aliases, the broader, narrower (one level) and related topics, each with how many people know it.</summary>
    public async Task<TopicView?> TopicAsync(UserAccount actor, bool aiScopeOnly, Guid tagId, CancellationToken ct)
    {
        // The linked topics the reader may know of only (VisibleTags): a tag of pages the reader cannot read names nothing.
        var rows = await session.ReadRowsAsync($$"""
            {{PageTagStore.AccessiblePages}}, {{VisibleTags}}
            SELECT 'topic', t."Id", t."Name", 0 FROM "Tag" t WHERE t."Id" = :tag AND t."MergedIntoId" IS NULL
            UNION ALL SELECT 'alias', a."TagId", a."Name", 0 FROM "TagAlias" a WHERE a."TagId" = :tag
            UNION ALL SELECT 'parent', t."Id", t."Name", COALESCE(p.n, 0)::int FROM "TagRelation" r JOIN "Tag" t ON t."Id" = r."ParentId" LEFT JOIN people p ON p."TagId" = t."Id"
              WHERE r."TagId" = :tag AND r."Kind" = 'part_of' AND t."Id" IN (SELECT "TagId" FROM visible)
            UNION ALL SELECT 'child', t."Id", t."Name", COALESCE(p.n, 0)::int FROM "TagRelation" r JOIN "Tag" t ON t."Id" = r."TagId" LEFT JOIN people p ON p."TagId" = t."Id"
              WHERE r."ParentId" = :tag AND r."Kind" = 'part_of' AND t."Id" IN (SELECT "TagId" FROM visible)
            UNION ALL SELECT 'related', t."Id", t."Name", COALESCE(p.n, 0)::int FROM "TagRelation" r
              JOIN "Tag" t ON t."Id" = CASE WHEN r."TagId" = :tag THEN r."ParentId" ELSE r."TagId" END LEFT JOIN people p ON p."TagId" = t."Id"
              WHERE r."Kind" = 'related' AND (r."TagId" = :tag OR r."ParentId" = :tag) AND t."Id" IN (SELECT "TagId" FROM visible)
            """, new Dictionary<string, object> { ["tag"] = tagId, ["actor"] = actor.Id, ["stamp"] = actor.SecurityStamp, ["ai"] = aiScopeOnly }, row => (Kind: row.GetString(0), Id: row.GetGuid(1), Name: row.GetString(2), People: row.GetInt32(3)), ct);
        if (rows.FirstOrDefault(x => x.Kind == "topic") is not { Name: not null } topic)
        {
            return null;
        }
        TopicRef[] Of(string kind) => rows.Where(x => x.Kind == kind).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).Select(x => new TopicRef(x.Id, x.Name, x.People)).ToArray();
        return new TopicView(topic.Id, topic.Name, rows.Where(x => x.Kind == "alias").Select(x => x.Name).Order(StringComparer.CurrentCultureIgnoreCase).ToArray(),
            Of("parent"), Of("child"), Of("related"));
    }

    /// <summary>The rater's rating replaced (or removed with a null level).</summary>
    public async Task SaveAsync(Guid contactId, Guid tagId, Guid raterId, int? level, string visibility, string? note, CancellationToken ct)
    {
        if (level is null)
        {
            await session.CreateSQLQuery("""DELETE FROM "SkillRating" WHERE "ContactId" = :contact AND "TagId" = :tag AND "RaterId" = :rater""")
                .SetParameter("contact", contactId).SetParameter("tag", tagId).SetParameter("rater", raterId).ExecuteUpdateAsync(ct);
            return;
        }
        await session.CreateSQLQuery("""
            INSERT INTO "SkillRating"("Id","ContactId","TagId","RaterId","Level","Visibility","Note","UpdatedAt")
            VALUES (:id, :contact, :tag, :rater, :level, :visibility, :note, :now)
            ON CONFLICT ("ContactId","TagId","RaterId") DO UPDATE SET "Level" = EXCLUDED."Level", "Visibility" = EXCLUDED."Visibility",
              "Note" = EXCLUDED."Note", "UpdatedAt" = EXCLUDED."UpdatedAt"
            """).SetParameter("id", Guid.CreateVersion7()).SetParameter("contact", contactId).SetParameter("tag", tagId).SetParameter("rater", raterId)
            .SetParameter("level", (short)level.Value).SetParameter("visibility", visibility).SetParameter("note", note, NHibernateUtil.String)
            .SetParameter("now", DateTime.UtcNow, NHibernateUtil.UtcDateTime).ExecuteUpdateAsync(ct);
    }

    /// <summary>
    /// Merges tag :from into :into: the tags merged into :from and its aliases follow, its name becomes an alias, its ratings move (one rater's
    /// two ratings of a person keep the higher level and the more private visibility: private &lt; shared &lt; signed, so a private one never
    /// turns shared), its broader and narrower links move, and its current page assignments close and open
    /// for :into where the page does not carry it yet (the history of :from stays).
    /// </summary>
    public async Task MergeAsync(Guid from, Guid into, Guid actor, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await session.CreateSQLQuery("""
            UPDATE "Tag" SET "MergedIntoId" = :into WHERE "Id" = :from OR "MergedIntoId" = :from;
            UPDATE "TagAlias" SET "TagId" = :into WHERE "TagId" = :from;
            INSERT INTO "TagAlias"("Id","TagId","Name","NormalizedName","CreatedAt","CreatedById")
              SELECT :alias, :into, t."Name", t."NormalizedName", :now, :actor FROM "Tag" t WHERE t."Id" = :from
              ON CONFLICT ("NormalizedName") DO NOTHING;
            INSERT INTO "SkillRating"("Id","ContactId","TagId","RaterId","Level","Visibility","Note","UpdatedAt")
              SELECT gen_random_uuid(), r."ContactId", :into, r."RaterId", r."Level", r."Visibility", r."Note", r."UpdatedAt" FROM "SkillRating" r WHERE r."TagId" = :from
              ON CONFLICT ("ContactId","TagId","RaterId") DO UPDATE SET "Level" = GREATEST("SkillRating"."Level", EXCLUDED."Level"),
                "Note" = COALESCE("SkillRating"."Note", EXCLUDED."Note"), "Visibility" = LEAST("SkillRating"."Visibility", EXCLUDED."Visibility");
            DELETE FROM "SkillRating" WHERE "TagId" = :from;
            INSERT INTO "TagRelation"("TagId","ParentId","Kind","CreatedAt","CreatedById")
              SELECT CASE WHEN r."TagId" = :from THEN :into ELSE r."TagId" END, CASE WHEN r."ParentId" = :from THEN :into ELSE r."ParentId" END, r."Kind", r."CreatedAt", r."CreatedById"
              FROM "TagRelation" r WHERE (r."TagId" = :from OR r."ParentId" = :from)
                AND (CASE WHEN r."TagId" = :from THEN :into ELSE r."TagId" END) <> (CASE WHEN r."ParentId" = :from THEN :into ELSE r."ParentId" END)
              ON CONFLICT DO NOTHING;
            DELETE FROM "TagRelation" WHERE "TagId" = :from OR "ParentId" = :from;
            WITH closed AS (
              UPDATE "PageTag" SET "ActiveTo" = GREATEST(:now, "ActiveFrom" + interval '1 millisecond') WHERE "TagId" = :from AND "ActiveTo" IS NULL RETURNING "PageId", "ActiveTo"
            )
            INSERT INTO "PageTag"("Id","PageId","TagId","CreatedById","CreatedAt","ActiveFrom")
              SELECT gen_random_uuid(), c."PageId", :into, :actor, :now,
                GREATEST(c."ActiveTo", COALESCE((SELECT max(x."ActiveTo") FROM "PageTag" x WHERE x."PageId" = c."PageId" AND x."TagId" = :into), c."ActiveTo"))
              FROM closed c WHERE NOT EXISTS (SELECT 1 FROM "PageTag" x WHERE x."PageId" = c."PageId" AND x."TagId" = :into AND x."ActiveTo" IS NULL);
            """).SetParameter("from", from).SetParameter("into", into).SetParameter("actor", actor).SetParameter("alias", Guid.CreateVersion7())
            .SetParameter("now", now, NHibernateUtil.UtcDateTime).ExecuteUpdateAsync(ct);
    }

    /// <summary>Whether <paramref name="parent"/> is <paramref name="tag"/> or lies under it (a link would make a cycle).</summary>
    public async Task<bool> WouldCycleAsync(Guid tag, Guid parent, CancellationToken ct)
        => (await session.ReadRowsAsync("""
            WITH RECURSIVE up AS (SELECT CAST(:parent AS uuid) AS id UNION SELECT r."ParentId" FROM "TagRelation" r JOIN up ON r."TagId" = up.id AND r."Kind" = 'part_of')
            SELECT EXISTS (SELECT 1 FROM up WHERE id = :tag)
            """, new Dictionary<string, object> { ["tag"] = tag, ["parent"] = parent }, row => row.GetBoolean(0), ct)).Single();

    /// <summary>The link of <paramref name="kind"/> set or removed; a related pair is one row, the smaller id first, so either order finds it.</summary>
    public async Task LinkAsync(Guid tag, Guid parent, string kind, bool remove, Guid actor, CancellationToken ct)
    {
        if (kind == "related" && tag.CompareTo(parent) > 0)
        {
            (tag, parent) = (parent, tag);
        }
        await (remove
            ? session.CreateSQLQuery("""DELETE FROM "TagRelation" WHERE "TagId" = :tag AND "ParentId" = :parent AND "Kind" = :kind""")
            : session.CreateSQLQuery("""
                INSERT INTO "TagRelation"("TagId","ParentId","Kind","CreatedAt","CreatedById") VALUES (:tag, :parent, :kind, :now, :actor)
                ON CONFLICT ("TagId","ParentId") DO UPDATE SET "Kind" = EXCLUDED."Kind"
                """).SetParameter("now", DateTime.UtcNow, NHibernateUtil.UtcDateTime).SetParameter("actor", actor))
            .SetParameter("tag", tag).SetParameter("parent", parent).SetParameter("kind", kind).ExecuteUpdateAsync(ct);
    }

    /// <summary>The person–topic pairs the reader may see a rating of (at most 3000), in topics of readable pages of <paramref name="spaceId"/> when given.</summary>
    public Task<List<(Guid ContactId, Guid TagId)>> GraphPairsAsync(UserAccount actor, bool aiScopeOnly, Guid? spaceId, CancellationToken ct)
        => session.ReadRowsAsync($$"""
            {{PageTagStore.AccessiblePages}}
            SELECT DISTINCT r."ContactId", r."TagId" FROM "SkillRating" r JOIN "Tag" t ON t."Id" = r."TagId" AND t."MergedIntoId" IS NULL
            WHERE (r."Visibility" <> 'private' OR r."RaterId" = :actor)
              AND (NOT :inspace OR EXISTS (SELECT 1 FROM "PageTag" pt JOIN accessible p ON p."Id" = pt."PageId"
                WHERE pt."TagId" = r."TagId" AND pt."ActiveTo" IS NULL AND p."SpaceId" = :space))
            LIMIT 3000
            """, new Dictionary<string, object>
            {
                ["actor"] = actor.Id, ["stamp"] = actor.SecurityStamp, ["ai"] = aiScopeOnly, ["inspace"] = spaceId is not null, ["space"] = spaceId ?? Guid.Empty
            }, row => (row.GetGuid(0), row.GetGuid(1)), ct);

    /// <summary>The links among :tags: part_of (narrower → broader) and related.</summary>
    public Task<List<(Guid TagId, Guid ParentId, string Kind)>> RelationsAmongAsync(Guid[] tags, CancellationToken ct)
        => session.ReadRowsAsync("""
            SELECT "TagId", "ParentId", "Kind" FROM "TagRelation" WHERE "TagId" = ANY(:tags) AND "ParentId" = ANY(:tags)
            """, new Dictionary<string, object> { ["tags"] = tags }, row => (row.GetGuid(0), row.GetGuid(1), row.GetString(2)), ct);

    /// <summary>A merged contact's ratings go to the contact it went into (MergeContactsHandler); one rater's two keep the higher level and the more private visibility (as <see cref="MergeAsync"/>).</summary>
    public static Task MoveContactAsync(ISession session, Guid from, Guid into, CancellationToken ct)
        => session.CreateSQLQuery("""
            INSERT INTO "SkillRating"("Id","ContactId","TagId","RaterId","Level","Visibility","Note","UpdatedAt")
              SELECT gen_random_uuid(), :into, r."TagId", r."RaterId", r."Level", r."Visibility", r."Note", r."UpdatedAt" FROM "SkillRating" r WHERE r."ContactId" = :from
              ON CONFLICT ("ContactId","TagId","RaterId") DO UPDATE SET "Level" = GREATEST("SkillRating"."Level", EXCLUDED."Level"),
                "Note" = COALESCE("SkillRating"."Note", EXCLUDED."Note"), "Visibility" = LEAST("SkillRating"."Visibility", EXCLUDED."Visibility");
            DELETE FROM "SkillRating" WHERE "ContactId" = :from;
            """).SetParameter("from", from).SetParameter("into", into).ExecuteUpdateAsync(ct);

    /// <summary>The account's own live contact (its directoryId), null without one.</summary>
    public async Task<Guid?> OwnContactAsync(Guid account, CancellationToken ct)
        => (await session.ReadRowsAsync("""
            SELECT c."Id" FROM "ContentResource" c WHERE c."TypeId" = 'atlas.contact' AND c."TrashedAt" IS NULL AND c."Scope" <> 'private'
              AND lower(c."Data"->>'directoryId') = CAST(:account AS text) ORDER BY c."Id" LIMIT 1
            """, new Dictionary<string, object> { ["account"] = account }, row => (Guid?)row.GetGuid(0), ct)).SingleOrDefault();

    /// <summary>Whether the contact is the account's own contact (<see cref="OwnContactAsync"/>).</summary>
    public async Task<bool> IsOwnAsync(Guid account, Guid contact, CancellationToken ct) => await OwnContactAsync(account, ct) == contact;

    /// <summary>Removes a topic from a person for everyone (the person or a wiki admin, SkillsHandler): every rating others may count and
    /// the remover's own. Others' private ones stay theirs: nobody else sees them.</summary>
    public Task RemoveTopicAsync(Guid contactId, Guid tagId, Guid actor, CancellationToken ct)
        => session.CreateSQLQuery("""
            DELETE FROM "SkillRating" WHERE "ContactId" = :contact AND "TagId" = :tag AND ("Visibility" <> 'private' OR "RaterId" = :actor)
            """).SetParameter("contact", contactId).SetParameter("tag", tagId).SetParameter("actor", actor).ExecuteUpdateAsync(ct);

    /// <summary>Whether the tag is on a page (now) the reader may not edit, or (MCP) one of a space out of AI: a merge would retag it.</summary>
    public async Task<bool> TaggedBeyondAsync(UserAccount actor, bool aiScopeOnly, Guid tagId, CancellationToken ct)
        => (await session.ReadRowsAsync($$"""
            SELECT EXISTS (SELECT 1 FROM "PageTag" pt JOIN "Page" p ON p."Id" = pt."PageId" WHERE pt."TagId" = :tag AND pt."ActiveTo" IS NULL
              AND (:ai AND NOT {{AccessSql.SpaceInAi("p.\"SpaceId\"")}}
                OR NOT EXISTS (SELECT 1 FROM "EffectivePageAccess" e WHERE e."PageId" = pt."PageId" AND e."UserId" = :actor AND e."Role" >= 2)))
            """, new Dictionary<string, object> { ["tag"] = tagId, ["actor"] = actor.Id, ["ai"] = aiScopeOnly }, row => row.GetBoolean(0), ct)).Single();

    /// <summary>Whether the reader is a wiki admin (locally or by a verified Hub group): an admin sees every shared rating with its rater.</summary>
    public async Task<bool> IsWikiAdminAsync(UserAccount actor, CancellationToken ct)
        => (await session.ReadRowsAsync($"WITH {AccessSql.ActorCte} SELECT COALESCE((SELECT \"IsWikiAdmin\" FROM actor), false)",
            new Dictionary<string, object> { ["actor"] = actor.Id, ["stamp"] = actor.SecurityStamp }, row => row.GetBoolean(0), ct)).Single();

    private static int? Int(DbDataReader row, int column) => row.IsDBNull(column) ? null : Convert.ToInt32(row.GetValue(column));

    private static string? Text(DbDataReader row, int column) => row.IsDBNull(column) ? null : row.GetString(column);
}
