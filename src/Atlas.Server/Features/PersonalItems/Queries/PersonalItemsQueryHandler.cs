using System.Text.RegularExpressions;
using NHibernate;
using Atlas.Server.Features.PersonalItems.Contracts;
using Atlas.Server.Features.PersonalItems.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalItems.Queries;

/// <summary>Můj prostor: the caller's own live personal items, newest change first. Its trash is the object trash (scope private).</summary>
public sealed record ListPersonalItems;

/// <summary>Sdíleno se mnou: the live personal items of others the caller holds a grant on now, newest change first.</summary>
public sealed record ListSharedWithMe;

/// <summary>
/// The two lists of personal items (spec 2026-10-29 §3), one statement each over the envelopes, with a note's excerpt instead of its
/// text (PERF1-2). Můj prostor reads the owner's envelopes by IX_ContentResource_Owner. Sdíleno se mnou finds the objects the caller
/// holds a grant on now through the subject indexes (its own grants, then its groups', UNION ALL: an OR read every grant), and
/// AccessSql.ObjectGrantRole, the one rule of a grantee's access, decides the level; it never lists the caller's own items. Only the
/// personal types an enabled module registers are listed.
/// </summary>
// shortcut: each list returns the newest 200 and the web filters them by title and excerpt; page them once someone keeps more.
public sealed partial class PersonalItemsQueryHandler(ISession session, IContentAccess access, PersonalItemTargets targets)
{
    public const int Limit = 200;
    public const int ExcerptLength = 200;
    // Enough Markdown for ExcerptLength characters of text once its marks are gone.
    internal const int ExcerptSource = 3 * ExcerptLength;

    public async Task<IReadOnlyList<PersonalItemDto>> Handle(ListPersonalItems query, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(null, ct);
        var rows = await session.CreateSQLQuery("""
                SELECT r."Id", r."TypeId", COALESCE(r."Title", ''), r."UpdatedAt", r."Version", left(r."Data" ->> 'text', :source + 1),
                       CAST((SELECT count(*) FROM "EffectiveResourceGrant" g WHERE g."ObjectId" = r."Id") AS int), r."TrashedAt"
                FROM "ContentResource" r
                WHERE r."Scope" = 'private' AND r."OwnerId" = :me AND r."TrashedAt" IS NULL
                  AND r."TypeId" = ANY(CAST(string_to_array(:types, ',') AS text[]))
                ORDER BY COALESCE(r."TrashedAt", r."UpdatedAt") DESC, r."Id" LIMIT :take
                """)
            .SetParameter("me", actor.Id).SetParameter("types", string.Join(',', targets.TypeIds())).SetParameter("take", Limit)
            .SetParameter("source", ExcerptSource)
            .ListAsync<object[]>(ct);
        return rows.Select(row => new PersonalItemDto((Guid)row[0], (string)row[1], (string)row[2], Utc(row[3]), Convert.ToInt64(row[4]), true,
            (int)row[6], null, null, null, Excerpt(row[5]), row[7] is DateTime trashed ? Utc(trashed) : null)).ToArray();
    }

    public async Task<IReadOnlyList<PersonalItemDto>> Handle(ListSharedWithMe query, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(null, ct);
        var rows = await session.CreateSQLQuery($$"""
                WITH held AS (
                    SELECT g."ObjectId", g."ExpiresAt" FROM "EffectiveResourceGrant" g WHERE g."UserId" = :me AND g."ObjectId" IS NOT NULL
                    UNION ALL
                    SELECT g."ObjectId", g."ExpiresAt" FROM "EffectiveResourceGrant" g
                    JOIN "EffectiveGroupMember" m ON m."GroupId" = g."GroupId" AND m."UserId" = :me
                    WHERE g."ObjectId" IS NOT NULL),
                mine AS (
                    SELECT held."ObjectId", {{AccessSql.ObjectGrantRole("held.\"ObjectId\"", ":me")}} AS role,
                           CASE WHEN bool_or(held."ExpiresAt" IS NULL) THEN NULL ELSE max(held."ExpiresAt") END AS ends
                    FROM held GROUP BY held."ObjectId")
                SELECT r."Id", r."TypeId", COALESCE(r."Title", ''), r."UpdatedAt", r."Version", left(r."Data" ->> 'text', :source + 1), o."DisplayName",
                       mine.role, mine.ends
                FROM mine JOIN "ContentResource" r ON r."Id" = mine."ObjectId" JOIN "UserAccount" o ON o."Id" = r."OwnerId"
                WHERE mine.role IS NOT NULL AND r."Scope" = 'private' AND r."OwnerId" <> :me AND r."TrashedAt" IS NULL
                  AND r."TypeId" = ANY(CAST(string_to_array(:types, ',') AS text[]))
                ORDER BY r."UpdatedAt" DESC, r."Id" LIMIT :take
                """)
            .SetParameter("me", actor.Id).SetParameter("types", string.Join(',', targets.TypeIds())).SetParameter("take", Limit)
            .SetParameter("source", ExcerptSource)
            .ListAsync<object[]>(ct);
        return rows.Select(row =>
        {
            var write = Convert.ToInt32(row[7]) >= 2;
            return new PersonalItemDto((Guid)row[0], (string)row[1], (string)row[2], Utc(row[3]), Convert.ToInt64(row[4]), write, 0, (string)row[6],
                write ? "write" : "read", row[8] is DateTime ends ? Utc(ends) : null, Excerpt(row[5]));
        }).ToArray();
    }

    private static DateTime Utc(object value) => DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc);

    /// <summary>The start of a note's text as plain text on one line, cut at ExcerptLength with an ellipsis; from at most
    /// ExcerptSource + 1 characters of it (the one past says it was cut).</summary>
    internal static string? Excerpt(object? markdown)
    {
        if (markdown is not string text)
        {
            return null;
        }
        var cut = text.Length > ExcerptSource;
        var plain = Spaces().Replace(Markdig.Markdown.ToPlainText(cut ? text[..ExcerptSource] : text), " ").Trim();
        return plain.Length > ExcerptLength ? plain[..ExcerptLength].TrimEnd() + "…" : cut ? plain + "…" : plain;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
