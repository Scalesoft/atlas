namespace Atlas.Server.Features.Search.Domain;

/// <summary>
/// The window of a search page (the search and the tag listing): 1–50 results from any offset. There is no last page but the
/// results' own end (infinite loading, 2026-10-28; it was 1000). The offset only has to leave room for the window and its
/// look-ahead row in a PostgreSQL integer (<c>p_skip+p_take</c>).
/// </summary>
public static class SearchPaging
{
    public const string Message = "Počet výsledků musí být 1–50 a posun nezáporný.";

    public static bool Valid(int offset, int limit) => limit is >= 1 and <= 50 && offset >= 0 && offset <= int.MaxValue - limit - 1;
}
