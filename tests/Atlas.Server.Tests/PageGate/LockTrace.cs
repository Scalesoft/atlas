using System.Text.RegularExpressions;

namespace Atlas.Server.Tests.PageGate;

/// <summary>
/// Reduces recorded SQL to the steps the content lock order is about: advisory locks, row locks with their table and strength,
/// the actor variable, and every unlocked read and every write of a Page or Space row. Everything else (grants, revisions,
/// projections) is left out, so a trace changes only when the order, a strength or the place of an unlocked read changes.
/// </summary>
/// <remarks>
/// The statement text carries no parameter values, so a step names the table, not the row: "advisory shared" is the content
/// lock 731145012 (its only shared user), "advisory exclusive (text key)" a hashed text key such as the personal lock.
/// What a trace therefore cannot see: which space or page a step locks and whether two rows of a table are taken in ascending
/// id order; which numeric advisory key is taken (the exclusive content lock and every other numeric key are both "advisory
/// exclusive (number)"); whether a "Page FOR UPDATE" loads the row or only locks it, so not the stale read of an instance the
/// session already held; locks taken inside triggers and by foreign keys; a
/// row lock that is not the end of its statement (FOR UPDATE OF, NOWAIT, SKIP LOCKED), which shows as an unlocked read of a
/// Space or Page and not at all for another table; any table of a select but its first; and a write inside a WITH statement.
/// </remarks>
internal static partial class LockTrace
{
    private static readonly string[] GateTables = ["Space", "Page"];

    public static IReadOnlyList<string> Of(IEnumerable<string> statements)
    {
        return statements.Select(Step).OfType<string>().ToArray();
    }

    /// <summary>
    /// The steps that take a Space or Page row exclusively after the transaction held rows of that table only FOR SHARE: the
    /// upgrade two transactions deadlock on (40P01). FOR UPDATE and UPDATE count, and so does a Page insert for Space, which takes
    /// the space FOR UPDATE right before it (ContentRowEffects, its own step). DELETE, FOR NO KEY UPDATE and FOR KEY SHARE do not count.
    /// </summary>
    /// <remarks>
    /// Decided per table, not per row, which is wrong in both directions. FOR SHARE on one row and FOR UPDATE on another is
    /// reported though no row is upgraded. And nothing is reported once the transaction took any row of the table exclusively:
    /// "Space A FOR UPDATE, Space B FOR SHARE, insert Page into B" upgrades B and passes.
    /// </remarks>
    public static IReadOnlyList<string> Upgrades(IReadOnlyList<string> steps)
    {
        var upgrades = new List<string>();
        foreach (var table in GateTables)
        {
            var shared = false;
            var exclusive = false;
            foreach (var step in steps)
            {
                var takesExclusive = step == $"{table} FOR UPDATE" || step == $"update {table}" || table == "Space" && step == "insert Page";
                if (takesExclusive && shared && !exclusive)
                {
                    upgrades.Add($"{step} after {table} FOR SHARE");
                }
                exclusive |= takesExclusive;
                shared |= step == $"{table} FOR SHARE";
            }
        }
        return upgrades;
    }

    /// <summary>
    /// A statement on one line as it was sent, with the column list of an entity load left out ("SELECT ... FROM"): its aliases
    /// carry positions of the mapping (Id1_36_0_), which move with any mapped class. What stays is what the statement reads,
    /// by which key and under which lock, so a ladder can be spelled out in SQL and compared text for text.
    /// </summary>
    public static string Shape(string sql)
    {
        return EntityColumns().Replace(string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), "SELECT ... FROM ");
    }

    private static string? Step(string sql)
    {
        if (sql.Contains("pg_advisory_xact_lock_shared(", StringComparison.Ordinal))
        {
            return "advisory shared";
        }
        if (sql.Contains("pg_advisory_xact_lock(hashtextextended(", StringComparison.Ordinal))
        {
            return "advisory exclusive (text key)";
        }
        if (sql.Contains("pg_advisory_xact_lock(", StringComparison.Ordinal))
        {
            return "advisory exclusive (number)";
        }
        if (sql.Contains("WHERE NOT EXISTS (SELECT 1 FROM \"RevokedBrowserSession\"", StringComparison.Ordinal))
        {
            return "set actor";
        }
        if (Write().Match(sql) is { Success: true } write)
        {
            return GateTables.Contains(write.Groups["table"].Value) ? $"{write.Groups["verb"].Value.ToLowerInvariant()} {write.Groups["table"].Value}" : null;
        }
        if (Select().Match(sql) is not { Success: true } select)
        {
            return null;
        }
        var table = select.Groups["table"].Value;
        if (RowLock().Match(sql) is { Success: true } rowLock)
        {
            return $"{(table.Length == 0 ? "?" : table)} FOR {Regex.Replace(rowLock.Groups["strength"].Value, @"\s+", " ").ToUpperInvariant()}";
        }
        if (!GateTables.Contains(table))
        {
            return null;
        }
        return SpaceOfPage().IsMatch(sql) ? "read Page.SpaceId" : $"read {table}";
    }

    // The first FROM of a SELECT; the table group stays empty when it does not name a table directly (a Subselect mapping, a function).
    [GeneratedRegex("""^\s*select\b.*?\bfrom\s+(?:"(?<table>\w+)"|\S)""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Select();

    // The end of the statement, or of the first of several sent at once (a page's insert locks its space so: ContentRowEffects).
    [GeneratedRegex("""\bfor\s+(?<strength>no\s+key\s+update|key\s+share|update|share)\s*(?:;|$)""", RegexOptions.IgnoreCase)]
    private static partial Regex RowLock();

    [GeneratedRegex(""""^\s*(?<verb>insert|update|delete)\s+(?:into\s+|from\s+)?"(?<table>\w+)"""", RegexOptions.IgnoreCase)]
    private static partial Regex Write();

    [GeneratedRegex("""^\s*select\s+\w+\."SpaceId"\s+as\s+\w+\s+from\s+"Page"\s""", RegexOptions.IgnoreCase)]
    private static partial Regex SpaceOfPage();

    // The select list of an entity load: every column under an alias of name, column number, class number and suffix.
    [GeneratedRegex("""^SELECT (?:\w+\."\w+" as [A-Za-z]+\d+_\d+_\d+_,? )+FROM """)]
    private static partial Regex EntityColumns();
}
