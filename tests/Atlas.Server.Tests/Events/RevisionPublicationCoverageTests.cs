using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Atlas.Server.Tests.Events;

/// <summary>
/// 3.3 plan §4.8 (gate B): no database trigger announces a revision since migration 2026110420; the code that writes one does, through
/// ContentRevisions (PageRevised, PagePublished, ObjectRevised, ContentRebuilt). The compiler cannot see a path that forgets it, so this
/// test reads the server's source: every file that writes a page's working revision, its published pointer or trash state, a revision's
/// content in place, or a stored object (a directory contact) and its data, either announces it or is listed below with the reason it
/// does not. The list is exact both ways: a new writer fails until it announces or is listed, and a listed file that no longer writes
/// or that announces now must leave it. Per file, not per statement: a second write in a file that announces already is the review's.
/// </summary>
[Trait("Category", "Guard")]
public sealed partial class RevisionPublicationCoverageTests
{
    /// <summary>Files that write revisions and announce nothing, relative to src/Atlas.Server, and why.</summary>
    private static readonly Dictionary<string, string> Silent = new(StringComparer.Ordinal)
    {
        ["Features/Boards/Commands/BoardTreeHandler.cs"] = "a board's trash state: boards announce nothing yet, no consumer reads them",
        ["Features/Boards/Commands/DeleteBoardHandler.cs"] = "a board's trash state: boards announce nothing yet, no consumer reads them",
        ["Features/Contacts/ContactLinkMigration.cs"] = "links the contacts of revisions in place, a batch of a run; the run requests the "
            + "catalog's rebuild job when it ends (ContactLinkRunCommands)",
        ["Platform/Persistence/ContentRowEffects.cs"] = "the envelope of a page or board row it writes beside the row; the writer of the "
            + "row announces it (ContentRevisions)",
        ["Features/PageOrganization/Domain/PagePurge.cs"] = "blanks the revisions of a purged page; the purge removes what was derived "
            + "from them itself (PurgeCoverageTests)",
    };

    [Fact]
    public void Every_path_that_writes_a_revision_announces_it_or_is_listed_with_its_reason()
    {
        var root = Path.Combine(RepositoryRoot(), "src", "Atlas.Server");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => (Name: Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), Text: File.ReadAllText(path)))
            .ToList();
        var writers = files.Where(file => Writes().IsMatch(file.Text)).ToList();
        Assert.True(writers.Count >= 15, "The scan sees no writers of revisions: " + string.Join(", ", writers.Select(file => file.Name)));

        var problems = writers.Where(file => !Announces().IsMatch(file.Text) && !Silent.ContainsKey(file.Name))
            .Select(file => $"{file.Name}: writes a revision without announcing it (ContentRevisions) and is not listed in Silent.")
            .Concat(Silent.Keys.Where(name => !writers.Any(file => file.Name == name)).Select(name => $"{name}: listed, but writes no revision."))
            .Concat(writers.Where(file => Silent.ContainsKey(file.Name) && Announces().IsMatch(file.Text))
                .Select(file => $"{file.Name}: listed, but announces now."))
            .ToArray();
        Assert.True(problems.Length == 0, string.Join("\n", problems));
    }

    /// <summary>A page's working revision, its published pointer or trash state (or a board's: the same property name), a revision's
    /// content in place, a stored object or its data; in C# and in SQL text (quotes escaped or not).</summary>
    [GeneratedRegex("""
        new\s+PageRevision\s*[({]
        | INSERT\s+INTO\s+\\?"PageRevision\\?"
        | UPDATE\s+\\?"PageRevision\\?"\s+SET\s[^;]*\\?"ContentJson\\?"
        | \.PublishedRevisionId\s*=(?![=>])
        | \.IsTrashed\s*=(?![=>])
        | UPDATE\s+\\?"Page\\?"\s+SET\s[^;]*\\?"(PublishedRevisionId|IsTrashed)\\?"\s*=
        | new\s+ContentResource\s*[({]
        | INSERT\s+INTO\s+\\?"ContentResource\\?"
        | \.Data\s*=(?![=>])
        | UPDATE\s+\\?"ContentResource\\?"\s+SET\s+\\?"Data\\?"
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Writes();

    [GeneratedRegex(@"\.(PageRevised|PagePublished|ObjectRevised|ContentRebuilt)\(")]
    private static partial Regex Announces();

    private static string RepositoryRoot([CallerFilePath] string path = "")
        => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path))))!;
}
