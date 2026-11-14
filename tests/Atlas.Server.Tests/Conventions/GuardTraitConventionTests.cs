using System.Runtime.CompilerServices;
using Xunit;

namespace Atlas.Server.Tests.Conventions;

/// <summary>
/// tests/changed.ps1 runs the repository-wide guards with every server change by their Category=Guard trait, because a change in any
/// feature folder can break a test that reads the whole server assembly. A guard without the trait is skipped there and fails only
/// on main. A test file that reads the types or the IL of the whole assembly (GetTypes, ServerIl) therefore carries the trait.
/// </summary>
[Trait("Category", "Guard")]
public sealed class GuardTraitConventionTests
{
    [Fact]
    public void A_test_that_reads_the_whole_server_assembly_is_tagged_Guard()
    {
        var files = Directory.EnumerateFiles(TestsDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && Path.GetFileName(path) != "ServerIl.cs")
            .Select(path => (Path: Path.GetRelativePath(TestsDirectory(), path), Text: File.ReadAllText(path))).ToArray();
        var untagged = files.Where(file => (file.Text.Contains(".GetTypes()", StringComparison.Ordinal) || file.Text.Contains("ServerIl.", StringComparison.Ordinal))
                && !file.Text.Contains("[Trait(\"Category\", \"Guard\")]", StringComparison.Ordinal))
            .Select(file => file.Path).ToArray();
        Assert.True(untagged.Length == 0, "Tag the guard tests [Trait(\"Category\", \"Guard\")]: " + string.Join(", ", untagged));
        // The scan sees the guards, so finding none untagged is not a scan that sees nothing.
        Assert.Contains(files, file => file.Text.Contains("ServerIl.", StringComparison.Ordinal));
    }

    private static string TestsDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(Path.GetDirectoryName(path))!;
}
