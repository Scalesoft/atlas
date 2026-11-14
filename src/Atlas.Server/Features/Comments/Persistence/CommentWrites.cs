using Atlas.Server.Features.Comments.Domain;
using Atlas.Server.Features.Search.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Comments.Persistence;

/// <summary>
/// What a comment's text derives, rewritten in the transaction of the command that writes it (migration 2026102790): its mentioned
/// accounts (those that exist) and its search document; a deleted comment keeps neither. One statement after the session's flush,
/// after the one that reads the mentions it had.
/// </summary>
public static class CommentWrites
{
    /// <returns>The accounts mentioned now and not before: whom the inbox tells (inbox spec §4), never again on an edit.</returns>
    public static async Task<IReadOnlyList<Guid>> DeriveAsync(ISession session, Comment comment, CancellationToken ct)
    {
        await session.FlushAsync(ct);
        var mentions = comment.IsDeleted ? [] : CommentText.Mentions(comment.Body);
        var before = mentions.Count == 0 ? [] : await session.CreateSQLQuery("""SELECT "UserId" FROM "CommentMention" WHERE "CommentId"=:comment""")
            .SetParameter("comment", comment.Id).ListAsync<Guid>(ct);
        await session.CreateSQLQuery($"""
                {Search.SearchDocuments.NoJit}
                DELETE FROM "CommentMention" WHERE "CommentId" = :comment;
                INSERT INTO "CommentMention"("CommentId", "UserId")
                SELECT :comment, u."Id" FROM "UserAccount" u WHERE u."Id" = ANY(CAST(string_to_array(:mentions, ',') AS uuid[]));
                {SearchRefreshSql("= :comment")}
                """)
            .SetParameter("comment", comment.Id).SetParameter("mentions", string.Join(',', mentions))
            .ExecuteUpdateAsync(ct);
        return mentions.Except(before).ToList();
    }

    /// <summary>The search documents of the comments whose id is <paramref name="comments"/> (a condition: "= :comment"): the lexemes
    /// of the text without its links' targets (<see cref="SearchSqlText"/>; atlas_refresh_comment_search before Atlas 3.6).</summary>
    public static string SearchRefreshSql(string comments) => $"""
        DELETE FROM "CommentSearchDocument" WHERE "CommentId" {comments};
        INSERT INTO "CommentSearchDocument"("CommentId", "Vector")
        SELECT c."Id", {SearchSqlText.Lexemes("body.text", SearchSqlText.Normalize("body.text"))}
        FROM "Comment" c CROSS JOIN LATERAL (SELECT {SearchSqlText.CommentText("c.\"Body\"")} AS text) body
        WHERE c."Id" {comments} AND c."DeletedAt" IS NULL;
        """;
}
