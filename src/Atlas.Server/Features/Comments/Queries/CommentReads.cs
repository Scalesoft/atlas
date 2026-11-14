using Atlas.Server.Features.Comments.Contracts;
using Atlas.Server.Features.Comments.Domain;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Comments.Queries;

/// <summary>The reads of an object's comments, each one statement over its indexes (migration 2026102790).</summary>
public static class CommentReads
{
    /// <summary>The live comments and the newest of them (IX_Comment_Live).</summary>
    public static async Task<CommentCount> CountAsync(ISession session, Guid objectId, CancellationToken ct)
    {
        var row = await session.CreateSQLQuery("""
                SELECT count(*) AS count, (SELECT n."Id" FROM "Comment" n WHERE n."ObjectId" = :object AND n."DeletedAt" IS NULL
                    ORDER BY n."CreatedAt" DESC, n."Id" DESC LIMIT 1) AS latest
                FROM "Comment" c WHERE c."ObjectId" = :object AND c."DeletedAt" IS NULL
                """)
            .AddScalar("count", NHibernateUtil.Int32).AddScalar("latest", NHibernateUtil.Guid)
            .SetParameter("object", objectId)
            .UniqueResultAsync<object[]>(ct);
        return new CommentCount((int)row[0], (Guid?)row[1]);
    }

    /// <summary>Up to <paramref name="take"/> comments, newest first, older than <paramref name="before"/> when given (by
    /// CreatedAt, then Id: IX_Comment_Timeline), deleted ones included, with their author's name.</summary>
    public static async Task<IReadOnlyList<CommentRow>> PageAsync(ISession session, Guid objectId, Guid? before, int take, CancellationToken ct)
    {
        var older = before is null ? "" : """
            AND (c."CreatedAt", c."Id") < (SELECT b."CreatedAt", b."Id" FROM "Comment" b WHERE b."Id" = :before AND b."ObjectId" = :object)
            """;
        var query = session.CreateSQLQuery($"""
                SELECT c."Id" AS id, c."AuthorId" AS author, u."DisplayName" AS name, c."Body" AS body, c."Anchor" AS anchor,
                       c."CreatedAt" AS created, c."EditedAt" AS edited, c."DeletedAt" AS deleted
                FROM "Comment" c JOIN "UserAccount" u ON u."Id" = c."AuthorId"
                WHERE c."ObjectId" = :object {older}
                ORDER BY c."CreatedAt" DESC, c."Id" DESC LIMIT :take
                """)
            .AddScalar("id", NHibernateUtil.Guid).AddScalar("author", NHibernateUtil.Guid).AddScalar("name", NHibernateUtil.String)
            .AddScalar("body", NHibernateUtil.StringClob).AddScalar("anchor", NHibernateUtil.String).AddScalar("created", NHibernateUtil.UtcDateTime)
            .AddScalar("edited", NHibernateUtil.UtcDateTime).AddScalar("deleted", NHibernateUtil.UtcDateTime)
            .SetParameter("object", objectId).SetParameter("take", take);
        if (before is { } cursor)
        {
            query.SetParameter("before", cursor);
        }
        return (await query.ListAsync<object[]>(ct)).Select(row => new CommentRow(new Comment
        {
            Id = (Guid)row[0], ObjectId = objectId, AuthorId = (Guid)row[1], Body = (string)row[3], Anchor = (string?)row[4], CreatedAt = (DateTime)row[5],
            EditedAt = (DateTime?)row[6], DeletedAt = (DateTime?)row[7]
        }, (string)row[2])).ToArray();
    }
}

/// <summary>A comment as read (not attached to the session) with its author's display name.</summary>
public sealed record CommentRow(Comment Comment, string AuthorName);
