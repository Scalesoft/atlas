using Xunit;

namespace Atlas.Server.Tests;

/// <summary>A theory over a disposable PostgreSQL database; skipped, or failing on CI, like <see cref="PostgreSqlFactAttribute"/> without one.</summary>
public sealed class PostgreSqlTheoryAttribute : TheoryAttribute
{
    public PostgreSqlTheoryAttribute()
    {
        Skip = PostgreSqlFactAttribute.SkipReason(Environment.GetEnvironmentVariable("CI"), Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTION"));
    }
}
