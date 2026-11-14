using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Spaces.Domain;
using Atlas.Server.Features.YouTrack;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Linking.Lexicon.Queries;

/// <summary>GET /api/spaces/{id}/lexicon: what the linking engine recognises while writing in a space (audit engine D5).</summary>
public sealed record GetSpaceLexicon(Guid SpaceId);

/// <summary>A person the reader may name: <c>Key</c> is the one identity key (<c>object:</c> a directory contact, else <c>user:</c> an
/// account); initials are derived by the client, never stored. <c>Uses</c> counts the readable published pages of the space naming it.</summary>
public sealed record LexiconPerson(string Key, Guid? ContactId, Guid? UserId, string Name, string[] Aliases, string? Org, string? Role, int Uses);

public sealed record LexiconPage(Guid Id, string Title);

/// <summary>The lexicon (web <c>linking/lexicon.ts</c>, editor <c>MarkdownLexicon</c>). <c>Truncated</c>: the people hit their cap, so the
/// client never turns a name into a chip on its own (engine §4.5); page titles and issue keys are only ever offered, so a page list at its
/// cap does not set it (a large space keeps its name chips). <c>Version</c> is the hash of the rest and the ETag.</summary>
public sealed record SpaceLexicon(string Version, bool Truncated, LexiconPerson[] People, LexiconPage[] Pages, string[] YouTrackProjects);

/// <summary>
/// Only what the reader may read: the space as its tree lists it (PageListing, 404 otherwise), its listed pages with the title the
/// reader sees (SpacePageList: published, own drafts, WikiAdmin), the wiki's directory contacts and this space's ones for a space role
/// (ListObjectsHandler's rule; private contacts never), and the active accounts holding a current grant of the space. An account that
/// is a directory contact (its directory id or an e-mail) is that contact. A constant number of statements, whatever the sizes.
/// </summary>
public sealed class SpaceLexiconHandler(ISession session, IContentAccess access, PageListing listing, YouTrackConfigurationReader youTrack)
{
    /// <summary>Rows of one list of people; one more read tells <see cref="SpaceLexicon.Truncated"/>, never a silent cut.</summary>
    public const int Cap = 1000;
    /// <summary>Page titles: every page of a large space (10 000 titles are a few hundred kB, cached by ETag on the client).</summary>
    public const int PageCap = 10_000;

    public async Task<SpaceLexicon> Handle(GetSpaceLexicon query, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(query.SpaceId, ct);
        var role = await listing.RequireSpaceAsync(actor, query.SpaceId, ct);
        var pages = await SpacePageList.ReadAsync(session, actor, query.SpaceId, 0, PageCap, ct);
        var contacts = await session.CreateSQLQuery("""
                SELECT c."Id", c."Title", c."Data" ->> 'organization' AS "Org", c."Data" ->> 'title' AS "Role", c."Data" ->> 'directoryId' AS "UserId",
                    CAST(c."Data" -> 'aliases' AS text) AS "Aliases", CAST(c."Data" -> 'emails' AS text) AS "Emails",
                    (SELECT CAST(count(*) AS int) FROM "PageObjectMention" m
                        JOIN "Page" p ON p."Id" = m."SourcePageId" AND p."PublishedRevisionId" = m."SourceRevisionId"
                        WHERE m."ObjectId" = c."Id" AND p."SpaceId" = :space AND NOT p."IsTrashed"
                            AND EXISTS (SELECT 1 FROM "EffectivePageAccess" e WHERE e."PageId" = p."Id" AND e."UserId" = :actor AND e."Role" >= 1)) AS "Uses"
                FROM "ContentResource" c
                WHERE c."TypeId" = :type AND c."TrashedAt" IS NULL
                    AND (c."Scope" = :wiki OR (:spaceReader AND c."Scope" = :spaceScope AND c."SpaceId" = :space))
                ORDER BY "Uses" DESC, lower(c."Title"), c."Id"
                """)
            .SetParameter("space", query.SpaceId).SetParameter("actor", actor.Id).SetParameter("type", ContactObjectType.Id)
            .SetParameter("wiki", ObjectScopes.Wiki).SetParameter("spaceScope", ObjectScopes.Space).SetParameter("spaceReader", role is not null)
            .SetMaxResults(Cap + 1).ListAsync<object[]>(ct);
        var accounts = await session.CreateSQLQuery("""
                SELECT u."Id", u."DisplayName", u."Email" FROM "UserAccount" u
                WHERE u."IsActive" AND EXISTS (SELECT 1 FROM "EffectiveSpaceGrant" g WHERE g."SpaceId" = :space AND (g."UserId" = u."Id"
                    OR g."GroupId" IN (SELECT gm."GroupId" FROM "EffectiveGroupMember" gm WHERE gm."UserId" = u."Id"
                        AND (gm."VerifiedUntil" IS NULL OR statement_timestamp() < gm."VerifiedUntil"))))
                ORDER BY lower(u."DisplayName"), u."Id"
                """)
            .SetParameter("space", query.SpaceId).SetMaxResults(Cap + 1).ListAsync<object[]>(ct);

        var people = contacts.Take(Cap).Select(row => new LexiconPerson($"object:{row[0]}", (Guid)row[0],
            Guid.TryParse(row[4] as string, out var user) ? user : null, (string?)row[1] ?? "", Strings(row[5]), Blank(row[2]), Blank(row[3]), (int)row[7])).ToList();
        var contactOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < people.Count; i++)
        {
            if (people[i].UserId is { } id) contactOf.TryAdd(id.ToString(), i);
            foreach (var email in Strings(contacts[i][6])) contactOf.TryAdd(email.Trim(), i);
        }
        foreach (var row in accounts.Take(Cap))
        {
            var id = (Guid)row[0];
            if (contactOf.TryGetValue(id.ToString(), out var index) || row[2] is string email && contactOf.TryGetValue(email.Trim(), out index))
            {
                people[index] = people[index] with { UserId = id };
                continue;
            }
            people.Add(new LexiconPerson($"user:{id}", null, id, (string)row[1], [], null, null, 0));
        }
        var lexicon = new SpaceLexicon("", contacts.Count > Cap || accounts.Count > Cap, people.ToArray(),
            pages.Select(page => new LexiconPage(page.Id, page.Title)).ToArray(),
            // Only a space reader may search the space's YouTrack projects (YouTrackSpaceAccess); a reader of single pages gets no keys.
            role is null ? [] : await youTrack.ProjectKeysAsync(query.SpaceId, ct));
        return lexicon with { Version = Hash(lexicon) };
    }

    private static string Hash(SpaceLexicon lexicon) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(lexicon)))[..20];

    private static string? Blank(object? value) => value is string { Length: > 0 } text ? text : null;

    /// <summary>A JSON array's strings (aliases, e-mails); anything else stored there is no string of it.</summary>
    private static string[] Strings(object? json) => json is string text && JsonNode.Parse(text) is JsonArray array
        ? array.OfType<JsonValue>().Select(item => item.TryGetValue<string>(out var value) ? value : null).OfType<string>().Where(value => value.Length > 0).ToArray()
        : [];
}
