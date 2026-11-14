using System.Runtime.CompilerServices;
using FluentMigrator;
using Xunit;

namespace Atlas.Server.Tests.Conventions;

/// <summary>
/// AGENTS.md, Testing: every database migration has a test in the server suite that runs it over existing rows (pattern:
/// FluentMigrationTests). The compiler cannot see that, so this test finds the version of every migration of the server in the
/// source of the tests; a migration whose number no test names fails here. A comment that names the version counts as naming it,
/// so the test class says which migration it covers. Migrations written before this test are listed below and nothing else may be:
/// the list is exact both ways, so a listed migration that gains a test must leave it.
/// </summary>
[Trait("Category", "Guard")]
public sealed class MigrationTestConventionTests
{
    /// <summary>The numbered migrations (1 to 32) predate the dated scheme and are covered together by FluentMigrationTests.</summary>
    private const long FirstDatedVersion = 2026090000;

    /// <summary>Migrations without a test of their own when this test was written (2026-10-30). Do not add to the list: write the test.</summary>
    private static readonly long[] WithoutTest =
    [
        2026091301, 2026092501, 2026092503, 2026092504, 2026092505, 2026092620,
        2026092621, 2026092622, 2026092703, 2026092704, 2026092710, 2026092720,
        2026092730, 2026092740, 2026092800, 2026092830, 2026092831, 2026092910,
        2026092960, 2026100101, 2026100102, 2026100103, 2026100104, 2026100410,
        2026100430, 2026100440, 2026100450, 2026100510, 2026100530, 2026100550,
        2026100560, 2026100570, 2026100580, 2026100601, 2026100620, 2026100630,
        2026100730, 2026100740, 2026100800, 2026100810, 2026100820, 2026100830,
        2026100840, 2026100850, 2026100860, 2026100880, 2026100900, 2026100910,
        2026100960, 2026101270, 2026102630, 2026102880,
    ];

    [Fact]
    public void Every_migration_is_named_by_a_test_or_listed_without_one()
    {
        var tests = string.Join('\n', Directory.EnumerateFiles(TestsDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && Path.GetFileName(path) != nameof(MigrationTestConventionTests) + ".cs")
            .Select(File.ReadAllText));
        var versions = typeof(AtlasMigrations).Assembly.GetTypes()
            .Select(type => type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().SingleOrDefault()?.Version)
            .Where(version => version >= FirstDatedVersion).Select(version => version!.Value).Order().ToArray();
        Assert.True(versions.Length > WithoutTest.Length, "The scan sees no migrations.");

        var named = versions.Where(version => tests.Contains(version.ToString(), StringComparison.Ordinal)).ToHashSet();
        var untested = versions.Where(version => !named.Contains(version) && !WithoutTest.Contains(version)).ToArray();
        Assert.True(untested.Length == 0, "Migrations without a test over existing rows (name the version in the test, as FluentMigrationTests does): " + string.Join(", ", untested));
        var covered = WithoutTest.Where(named.Contains).ToArray();
        Assert.True(covered.Length == 0, "Now named by a test, remove from WithoutTest: " + string.Join(", ", covered));
        var gone = WithoutTest.Except(versions).ToArray();
        Assert.True(gone.Length == 0, "Listed, but no such migration: " + string.Join(", ", gone));
    }

    private static string TestsDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(Path.GetDirectoryName(path))!;
}
