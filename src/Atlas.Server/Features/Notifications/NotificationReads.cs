using System.Data.Common;
using Atlas.Server.Features.Notifications.Contracts;
using Atlas.Server.Features.Relations.Persistence;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Persistence;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Notifications;

/// <summary>
/// The recipient's inbox under the recipient's current rights (inbox spec §5). A page and the unread count are each one statement that
/// joins the rows to their subjects as the relations decide them (<see cref="RelationNodes"/>) and filters before the limit and the
/// count: an item whose subject the recipient may not read now (lost access, trashed, purged, a disabled module's type, on MCP a space
/// out of AI) is not there at all, no title, actor or count, and never pushes a readable item off a page or out of the count.
/// </summary>
public sealed class NotificationReads(ISession session, RelationNodes nodes, ObjectTypes types, IEnumerable<IJobDefinition> jobs)
{
    public const int PageSize = 30;
    /// <summary>The unread count stops at this many visible unread items; the client shows more as „99+“.</summary>
    public const int UnreadWindow = 100;
    /// <summary>The excerpt of a mentioning comment shown in the inbox: the start of its body is read in SQL, the plain text is cut here.</summary>
    private const int ExcerptLength = 120, ExcerptSource = 600;

    // shortcut: the subjects of the whole inbox (of every unread item for the count) are decided on each read, bounded by retention;
    // decide them per page in a lateral subquery if inboxes ever hold many thousands of items.
    private const string AllSubjects = """SELECT "SubjectId" FROM "Notification" WHERE "RecipientId"=:recipient""";

    private const string Node = """LEFT JOIN node v ON v."Id"=n."SubjectId" AND v.live AND v.readable""";

    /// <summary>An item the recipient may read now; an ended share also without access, but then without the subject's type, title,
    /// space or anchor (the recipient is told that a share ended, never what it was).</summary>
    private const string Shown = """n."RecipientId"=:recipient AND (v."Id" IS NOT NULL OR n."Kind"=:unshared OR n."Kind"=:jobFailed AND :wikiAdmin)""";

    private static readonly string PageSql = $"""
        WITH node AS ({RelationNodes.Over(AllSubjects)})
        SELECT n."Id",n."Kind",n."SubjectId",n."Anchor",n."ActorId",a."DisplayName",n."EventCount",n."CreatedAt",n."UpdatedAt",n."ReadAt" IS NOT NULL,
          v."Id" IS NOT NULL,v."TypeId",v.title,v."SpaceId",v."SpaceName",mc."Id",mc."Body"
        FROM "Notification" n LEFT JOIN "UserAccount" a ON a."Id"=n."ActorId" {Node}
        LEFT JOIN LATERAL (SELECT c."Id",left(c."Body",{ExcerptSource}) AS "Body" FROM "Comment" c JOIN "CommentMention" m ON m."CommentId"=c."Id"
            WHERE n."Kind"=:commentMention AND m."UserId"=n."RecipientId" AND c."ObjectId"=n."SubjectId" AND c."AuthorId"=n."ActorId" AND c."DeletedAt" IS NULL
            ORDER BY c."CreatedAt" DESC,c."Id" DESC LIMIT 1) mc ON TRUE
        WHERE {Shown} AND NOT EXISTS (SELECT 1 FROM "Notification" c WHERE c."Id"=:before AND c."RecipientId"=:recipient
            AND (n."UpdatedAt",n."Id")>=(c."UpdatedAt",c."Id"))
        ORDER BY n."UpdatedAt" DESC,n."Id" DESC LIMIT :limit
        """;

    private static readonly string UnreadSql = $"""
        WITH node AS ({RelationNodes.Over(AllSubjects + " AND \"ReadAt\" IS NULL")})
        SELECT CAST(count(*) AS int) FROM (SELECT 1 FROM "Notification" n {Node} WHERE {Shown} AND n."ReadAt" IS NULL LIMIT :window) x
        """;

    public async Task<NotificationList> PageAsync(UserAccount recipient, Guid? before, CancellationToken ct)
    {
        // Who is an administrator is read once for the page and its unread count (the SQL budget of /api/notifications).
        var admin = await EffectiveWikiAdmin.IsAdminAsync(session, recipient, ct);
        var parameters = Parameters(recipient, admin);
        parameters["before"] = before ?? Guid.Empty;
        parameters["limit"] = PageSize + 1;
        var rows = await session.ReadRowsAsync(PageSql, parameters, row => row.GetString(1) == NotificationKinds.JobFailed
            // A job is code, not content: its title from its definition, the job id in the anchor for the link to its page.
            ? new NotificationDto(row.GetGuid(0), row.GetString(1), row.GetGuid(2), "atlas.job", JobTitle(OptionalText(row, 3)), null, null,
                OptionalText(row, 3), OptionalId(row, 4), OptionalText(row, 5), row.GetInt32(6), Utc(row.GetDateTime(7)), Utc(row.GetDateTime(8)),
                row.GetBoolean(9))
            : row.GetBoolean(10)
            ? new NotificationDto(row.GetGuid(0), row.GetString(1), row.GetGuid(2), row.GetString(11), row.GetString(12), OptionalId(row, 13),
                OptionalText(row, 14), OptionalText(row, 3), OptionalId(row, 4), OptionalText(row, 5), row.GetInt32(6), Utc(row.GetDateTime(7)),
                Utc(row.GetDateTime(8)), row.GetBoolean(9), OptionalId(row, 15), OptionalText(row, 16) is { } body ? SearchText.Excerpt(body, ExcerptLength) : null)
            : new NotificationDto(row.GetGuid(0), row.GetString(1), row.GetGuid(2), null, "", null, null, null, OptionalId(row, 4), OptionalText(row, 5),
                row.GetInt32(6), Utc(row.GetDateTime(7)), Utc(row.GetDateTime(8)), row.GetBoolean(9)), ct);
        var items = rows.Take(PageSize).ToList();
        return new NotificationList(items, rows.Count > PageSize ? items[^1].Id : null, await UnreadAsync(recipient, admin, ct));
    }

    public async Task<int> UnreadAsync(UserAccount recipient, CancellationToken ct) =>
        await UnreadAsync(recipient, await EffectiveWikiAdmin.IsAdminAsync(session, recipient, ct), ct);

    private async Task<int> UnreadAsync(UserAccount recipient, bool admin, CancellationToken ct)
    {
        var parameters = Parameters(recipient, admin);
        parameters["window"] = UnreadWindow;
        return (await session.ReadRowsAsync(UnreadSql, parameters, row => row.GetInt32(0), ct))[0];
    }

    /// <param name="admin">An effective wiki administrator, Hub's included (EffectiveWikiAdmin): only they see job failures.</param>
    private Dictionary<string, object> Parameters(UserAccount recipient, bool admin)
    {
        var parameters = nodes.Parameters(recipient, null, [], types.TypeIds());
        parameters["recipient"] = recipient.Id;
        parameters["unshared"] = NotificationKinds.ItemUnshared;
        parameters["jobFailed"] = NotificationKinds.JobFailed;
        parameters["commentMention"] = NotificationKinds.CommentMention;
        parameters["wikiAdmin"] = admin;
        return parameters;
    }

    private string JobTitle(string? jobId) => jobs.FirstOrDefault(job => job.Id == jobId)?.Title ?? jobId ?? "";

    private static Guid? OptionalId(DbDataReader row, int column) => row.IsDBNull(column) ? null : row.GetGuid(column);

    private static string? OptionalText(DbDataReader row, int column) => row.IsDBNull(column) ? null : row.GetString(column);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
