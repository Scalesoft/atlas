using Atlas.Server.Features.Search.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards.Domain;

/// <summary>
/// A board's search document (migration 2026102640): the title and its live items' texts, rewritten in the transaction of the
/// command that changes them, so a rolled back batch leaves the index as it was. Not a trigger: a board's text spans many item
/// rows, and one rewrite per batch beats one per changed row.
/// </summary>
public static class BoardSearch
{
    /// <summary>Rewrites the document after writing what the session holds of the board.</summary>
    public static async Task RefreshAsync(ISession session, Guid boardId, CancellationToken ct)
    {
        await session.FlushAsync(ct);
        await session.CreateSQLQuery(Search.SearchDocuments.NoJit + RefreshSql("= :board")).SetParameter("board", boardId).ExecuteUpdateAsync(ct);
    }

    /// <summary>The indexed body's characters (L3): unique words of this much text stay well under PostgreSQL's 1 MB tsvector limit
    /// with their stems and identifier parts, so the refresh never fails the write; a larger board is found by its first texts.</summary>
    public const int MaxBodyCharacters = 100_000;

    /// <summary>The documents of the boards whose id is <paramref name="boards"/> (a condition: "= :board"): the title (A) and the live
    /// items' texts in their order (B, the first <see cref="MaxBodyCharacters"/>), with search's lexemes (<see cref="SearchSqlText"/>;
    /// atlas_refresh_board_search before Atlas 3.6).</summary>
    public static string RefreshSql(string boards) => $$"""
        INSERT INTO "BoardSearchDocument"("BoardId","Title","Body","Vector")
        SELECT d."Id",d."Title",d.body,
          setweight({{SearchSqlText.Lexemes("d.\"Title\"", SearchSqlText.Normalize("d.\"Title\""))}},'A')
          || setweight({{SearchSqlText.Lexemes("d.body", SearchSqlText.Normalize("d.body"))}},'B')
        FROM (
          SELECT b."Id",b."Title",COALESCE(left((
            SELECT string_agg(i."SearchText",E'\n\n' ORDER BY i."Z",i."Id") FROM "BoardItem" i
            WHERE i."BoardId"=b."Id" AND NOT i."IsDeleted" AND i."SearchText" IS NOT NULL),{{MaxBodyCharacters}}),'') AS body
          FROM "Board" b WHERE b."Id" {{boards}}) d
        ON CONFLICT ("BoardId") DO UPDATE SET "Title"=EXCLUDED."Title","Body"=EXCLUDED."Body","Vector"=EXCLUDED."Vector"
        """;
}
