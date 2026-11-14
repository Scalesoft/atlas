using System.Text.RegularExpressions;

namespace Atlas.Server.Tests.Search;

/// <summary>A search query moved out of the database in Atlas 3.6 (SearchQueries) with its parameters as SQL literals, for the tests of
/// older migrations that call it in plain SQL the way they called the function it replaced.</summary>
internal static class MovedSearch
{
    /// <summary><paramref name="text"/> with each <c>:name</c> replaced by its literal.</summary>
    public static string Bind(string text, params (string Name, string Literal)[] args)
    {
        foreach (var (name, literal) in args)
        {
            text = Regex.Replace(text, ":" + name + "(?![A-Za-z0-9_])", _ => literal);
        }
        return text;
    }

    /// <summary>The eight arguments every search shares (SearchQueryParameters), as literals.</summary>
    public static (string Name, string Literal)[] Lexical(string query, string prefix, string fts, bool phrase, bool fuzzy, string actor, string stamp)
        => [("query", $"'{query}'"), ("prefix", $"'{prefix}'"), ("fts", $"'{fts}'"), ("phrase", phrase ? "true" : "false"), ("fuzzy", fuzzy ? "true" : "false"),
            ("actor", $"CAST('{actor}' AS uuid)"), ("stamp", $"'{stamp}'"), ("space", "CAST(NULL AS uuid)")];

    /// <summary>The window of a filtered search: take, skip, no change date, not the latest first.</summary>
    public static (string Name, string Literal)[] Window(int take, int skip)
        => [("take", take.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("skip", skip.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("changed_since", "CAST(NULL AS timestamptz)"), ("latest", "false")];
}
