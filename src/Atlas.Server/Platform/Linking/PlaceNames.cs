using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync.Recognition;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Spaces.Domain;
using Atlas.Server.Platform.Resources;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Linking;

/// <summary>Something a place knows by name: <c>object:</c> a directory contact, <c>user:</c> an account without one, <c>page:</c> a
/// page. <paramref name="Id"/> is what a chip links to; <paramref name="Hint"/> tells two of one name apart (organization, role);
/// <paramref name="Uses"/> how often content of the place links it, which ranks candidates.</summary>
public sealed record PlaceEntity(string Key, string Kind, Guid Id, string Label, string? Hint, int Uses = 0);

/// <summary>A place's names for the server's recognition core, its <paramref name="Version"/> (a hash of them) and each entity by key.</summary>
public sealed class PlaceLexicon(string version, RecognitionLexicon lexicon, IReadOnlyDictionary<string, PlaceEntity> entities)
{
    private RecognitionIndex? m_index;

    public string Version { get; } = version;
    public RecognitionLexicon Lexicon { get; } = lexicon;
    public IReadOnlyDictionary<string, PlaceEntity> Entities { get; } = entities;

    /// <summary>Built once per lexicon, on first use.</summary>
    public RecognitionIndex Index => m_index ??= Lexicon.Index();

    /// <summary>The entity key of a recognition entry: people are keyed already, a page by its id.</summary>
    public static string KeyOf(RecognitionEntry entry) => entry.Kind == "page" ? "page:" + entry.Id : entry.Id;
}

/// <summary>
/// The names of a place (audit engine D5) as the server's recognition reads them. Without a reader: everything a reader of the space may
/// come to see (its directory contacts, the accounts with a grant, its published pages), for the extraction, which runs without rights;
/// the reads then keep only what their reader's own lexicon holds. With a reader: what that account may read, as the editor's lexicon
/// (<c>GET /api/spaces/{id}/lexicon</c>, SpaceLexiconHandler) decides it, without its cap on pages and with the people truncated only
/// when the people are. A constant number of statements.
/// </summary>
public sealed class PlaceNames(ISession session, PageListing listing)
{
    /// <summary>Rows of one list; one more tells <see cref="RecognitionLexicon.Truncated"/>, so nobody becomes a chip by a partial list.</summary>
    public const int Cap = 20_000;

    public async Task<PlaceLexicon> ReadAsync(Guid spaceId, UserAccount? reader, CancellationToken ct)
    {
        var spaceReader = reader is null || await listing.RequireSpaceAsync(reader, spaceId, ct) is not null;
        var contacts = await session.CreateSQLQuery("""
                SELECT c."Id", c."Title", c."Data" ->> 'organization' AS "Org", c."Data" ->> 'title' AS "Role", c."Data" ->> 'directoryId' AS "UserId",
                    CAST(c."Data" -> 'aliases' AS text) AS "Aliases", CAST(c."Data" -> 'emails' AS text) AS "Emails"
                FROM "ContentResource" c
                WHERE c."TypeId" = :type AND c."TrashedAt" IS NULL
                    AND (c."Scope" = :wiki OR (:spaceReader AND c."Scope" = :spaceScope AND c."SpaceId" = :space))
                ORDER BY c."Id"
                """)
            .SetParameter("space", spaceId).SetParameter("type", ContactObjectType.Id).SetParameter("wiki", ObjectScopes.Wiki)
            .SetParameter("spaceScope", ObjectScopes.Space).SetParameter("spaceReader", spaceReader)
            .SetMaxResults(Cap + 1).ListAsync<object[]>(ct);
        var accounts = await session.CreateSQLQuery("""
                SELECT u."Id", u."DisplayName", u."Email" FROM "UserAccount" u
                WHERE u."IsActive" AND EXISTS (SELECT 1 FROM "EffectiveSpaceGrant" g WHERE g."SpaceId" = :space AND (g."UserId" = u."Id"
                    OR g."GroupId" IN (SELECT gm."GroupId" FROM "EffectiveGroupMember" gm WHERE gm."UserId" = u."Id"
                        AND (gm."VerifiedUntil" IS NULL OR statement_timestamp() < gm."VerifiedUntil"))))
                ORDER BY u."Id"
                """)
            .SetParameter("space", spaceId).SetMaxResults(Cap + 1).ListAsync<object[]>(ct);
        IReadOnlyList<(Guid Id, string Title)> pages = reader is null
            ? (await session.CreateSQLQuery("""
                    SELECT p."Id", r."Title" FROM "Page" p JOIN "PageRevision" r ON r."Id" = p."PublishedRevisionId"
                    WHERE p."SpaceId" = :space AND NOT p."IsTrashed" AND NOT p."IsTemplate" ORDER BY p."Id"
                    """)
                .AddScalar("Id", NHibernateUtil.Guid).AddScalar("Title", NHibernateUtil.String)
                .SetParameter("space", spaceId).SetMaxResults(Cap + 1).ListAsync<object[]>(ct)).Select(row => ((Guid)row[0], (string)row[1])).ToArray()
            : (await SpacePageList.ReadAsync(session, reader, spaceId, 0, Cap + 1, ct)).Select(page => (page.Id, page.Title)).ToArray();

        // How often the place's content links each person and page (the graph): candidates best first. A count only, never shown.
        var uses = (await session.CreateSQLQuery("""
                SELECT r."TargetId", CAST(count(*) AS int) AS "Uses" FROM "ContentReference" r
                JOIN "ContentReferenceSource" s ON s."SourceId" = r."SourceId" AND s."SpaceId" = :space
                WHERE r."State" = 'linked' AND r."TargetId" IS NOT NULL AND r."RevisionKind" <> 'working'
                GROUP BY r."TargetId"
                """)
            .AddScalar("TargetId", NHibernateUtil.Guid).AddScalar("Uses", NHibernateUtil.Int32)
            .SetParameter("space", spaceId).ListAsync<object[]>(ct)).ToDictionary(row => (Guid)row[0], row => (int)row[1]);
        var entities = new Dictionary<string, PlaceEntity>(StringComparer.Ordinal);
        var people = new List<RecognitionPerson>();
        var contactOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in contacts.Take(Cap))
        {
            var (id, name) = ((Guid)row[0], (string?)row[1] ?? "");
            if (name.Length == 0)
            {
                continue;
            }
            foreach (var key in Strings(row[6]).Append(row[4] as string).OfType<string>())
            {
                contactOf.TryAdd(key.Trim(), people.Count);
            }
            people.Add(new RecognitionPerson($"object:{id}", id, Guid.TryParse(row[4] as string, out var user) ? user : null, name, Strings(row[5])));
            entities[$"object:{id}"] = new PlaceEntity($"object:{id}", "person", id, name, Hint(row[2], row[3]), uses.GetValueOrDefault(id));
        }
        // An account that is a directory contact (its directory id or an e-mail) is that contact (R3), else a person of its own.
        foreach (var row in accounts.Take(Cap))
        {
            var (id, name) = ((Guid)row[0], (string)row[1]);
            if (contactOf.ContainsKey(id.ToString()) || row[2] is string email && contactOf.ContainsKey(email.Trim()))
            {
                continue;
            }
            people.Add(new RecognitionPerson($"user:{id}", null, id, name, []));
            entities[$"user:{id}"] = new PlaceEntity($"user:{id}", "person", id, name, null, uses.GetValueOrDefault(id));
        }
        foreach (var (id, title) in pages.Take(Cap))
        {
            entities[$"page:{id}"] = new PlaceEntity($"page:{id}", "page", id, title, null, uses.GetValueOrDefault(id));
        }
        // shortcut: the place lexicon has no YouTrack project keys yet (SpaceLexiconHandler answers []), so issue keys are not recognized
        // on the server; read the projects from where the YouTrack settings store their short names once they do.
        var lexicon = new RecognitionLexicon(contacts.Count > Cap || accounts.Count > Cap, people,
            pages.Take(Cap).Select(page => new RecognitionPage(page.Id, page.Title)).ToArray(), []);
        return new PlaceLexicon(Hash(lexicon), lexicon, entities);
    }

    private static string Hash(RecognitionLexicon lexicon)
    {
        var text = new StringBuilder().Append(lexicon.Truncated).Append('\n');
        foreach (var person in lexicon.People)
        {
            text.Append(person.Key).Append('\t').Append(person.Name).Append('\t').AppendJoin('\t', person.Aliases).Append('\n');
        }
        foreach (var page in lexicon.Pages)
        {
            text.Append(page.Id).Append('\t').Append(page.Title).Append('\n');
        }
        text.AppendJoin('\t', lexicon.YouTrackProjects);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..20];
    }

    private static string? Hint(object? organization, object? role) =>
        string.Join(" · ", new[] { organization as string, role as string }.Where(value => !string.IsNullOrWhiteSpace(value))) is { Length: > 0 } hint ? hint : null;

    /// <summary>A JSON array's strings (aliases, e-mails); anything else stored there is no string of it.</summary>
    private static string[] Strings(object? json) => json is string text && JsonNode.Parse(text) is JsonArray array
        ? array.OfType<JsonValue>().Select(item => item.TryGetValue<string>(out var value) ? value : null).OfType<string>().Where(value => value.Length > 0).ToArray()
        : [];
}

/// <summary>
/// The extraction's lexicon of a place, kept a few seconds so an import's many revisions of one space read it once. A stale one is
/// harmless: the extraction stores the version it used, and the sweep extracts again whatever another version read.
/// </summary>
public sealed class PlaceLexiconCache(TimeProvider clock)
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, PlaceLexicon Lexicon)> m_places = new();

    public async Task<PlaceLexicon> GetAsync(Guid spaceId, PlaceNames names, CancellationToken ct)
    {
        if (m_places.TryGetValue(spaceId, out var known) && clock.GetUtcNow() - known.At < Fresh)
        {
            return known.Lexicon;
        }
        return Put(spaceId, await names.ReadAsync(spaceId, null, ct));
    }

    public PlaceLexicon Put(Guid spaceId, PlaceLexicon lexicon)
    {
        // The same version keeps the built index.
        if (m_places.TryGetValue(spaceId, out var known) && known.Lexicon.Version == lexicon.Version)
        {
            lexicon = known.Lexicon;
        }
        m_places[spaceId] = (clock.GetUtcNow(), lexicon);
        return lexicon;
    }
}
