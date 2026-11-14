using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Atlas.Server.Tests.Search;

/// <summary>
/// Atlas 3.6, no logic in the database: no trigger keeps the search documents since migration 2026110935; the code that changes what
/// they read rewrites them (SearchDocuments). The compiler cannot see a path that forgets it, so this test reads the server's source:
/// every file that writes a page revision (new, or its content, title or validity in place), a page's published pointer or space, a
/// publication or a Wiki.js import mapping either refreshes the documents or is listed below with the reason it need not. The list is
/// exact both ways, as in RevisionPublicationCoverageTests. Per file, not per statement: a second write in a file that refreshes is the
/// review's.
/// </summary>
[Trait("Category", "Guard")]
public sealed partial class SearchDocumentCoverageTests
{
    /// <summary>Files that write what the documents read and refresh nothing, relative to src/Atlas.Server, and why.</summary>
    private static readonly Dictionary<string, string> Silent = new(StringComparer.Ordinal)
    {
        ["Features/PageOrganization/Domain/PagePurge.cs"] = "blanks the revisions of a purged page and deletes its documents in the same statement",
    };

    [Fact]
    public void Every_path_that_changes_what_search_reads_refreshes_the_documents_or_is_listed_with_its_reason()
    {
        var root = Path.Combine(RepositoryRoot(), "src", "Atlas.Server");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => (Name: Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), Text: File.ReadAllText(path)))
            .ToList();
        // A revision writer must refresh the working document; a writer of the published pointer, space, publications or imports
        // any of them.
        var revisions = files.Where(file => RevisionWrites().IsMatch(file.Text) || file.Text.Contains("PageRevision", StringComparison.Ordinal)
            && InPlaceWrites().IsMatch(file.Text)).Select(file => file.Name).ToHashSet();
        var writers = files.Where(file => revisions.Contains(file.Name) || PublishedWrites().IsMatch(file.Text)).ToList();
        Assert.True(writers.Count >= 12, "The scan sees no writers: " + string.Join(", ", writers.Select(file => file.Name)));

        bool Refreshed((string Name, string Text) file) => (revisions.Contains(file.Name) ? WorkingRefreshes() : Refreshes()).IsMatch(file.Text);
        var problems = writers.Where(file => !Refreshed(file) && !Silent.ContainsKey(file.Name))
            .Select(file => $"{file.Name}: changes what search reads without refreshing the documents (SearchDocuments"
                + (revisions.Contains(file.Name) ? ".RefreshWorkingAsync or RefreshAsync" : "") + ") and is not listed in Silent.")
            .Concat(Silent.Keys.Where(name => !writers.Any(file => file.Name == name)).Select(name => $"{name}: listed, but writes nothing search reads."))
            .Concat(writers.Where(file => Silent.ContainsKey(file.Name) && Refreshed(file)).Select(file => $"{file.Name}: listed, but refreshes now."))
            .ToArray();
        Assert.True(problems.Length == 0, string.Join("\n", problems));
    }

    /// <summary>A page revision created, or its content, title or validity changed in place; in C# and in SQL text (quotes escaped or
    /// not).</summary>
    [GeneratedRegex("""
        new\s+PageRevision\s*[({]
        | INSERT\s+INTO\s+\\?"PageRevision\\?"
        | UPDATE\s+\\?"PageRevision\\?"\s+(\w+\s+)?SET\s[^;]*\\?"(ContentJson|Markdown|Title|ActiveTo|ActiveFrom)\\?"\s*=
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex RevisionWrites();

    /// <summary>A content, Markdown or title property set in C#: a revision's when the file uses PageRevision.</summary>
    [GeneratedRegex(@"\w+\.(Markdown|ContentJson|Title)\s*=(?![=>])")]
    private static partial Regex InPlaceWrites();

    /// <summary>A page's published pointer or space, a publication or an import mapping written, changed or deleted.</summary>
    [GeneratedRegex("""
        \.PublishedRevisionId\s*=(?![=>])
        | UPDATE\s+\\?"Page\\?"\s+(\w+\s+)?SET\s[^;]*\\?"(PublishedRevisionId|SpaceId)\\?"\s*=
        | \b(page|item)\.SpaceId\s*=(?![=>])
        | new\s+(PagePublication|WikiImportPage|WikiImportCheckpoint)\s*[({]
        | (DELETE\s+FROM|UPDATE)\s+\\?"(PagePublication|WikiImportPage|WikiImportCheckpoint)\\?"
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex PublishedWrites();

    [GeneratedRegex(@"SearchDocuments\.(RefreshAsync|RefreshWorkingAsync|RefreshPublishedAsync|RebuildAllSql)\b")]
    private static partial Regex Refreshes();

    /// <summary>The refreshes that rewrite the working document (the restore's rebuild rewrites both).</summary>
    [GeneratedRegex(@"SearchDocuments\.(RefreshAsync|RefreshWorkingAsync|RebuildAllSql)\b")]
    private static partial Regex WorkingRefreshes();

    private static string RepositoryRoot([CallerFilePath] string path = "")
        => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path))))!;
}
