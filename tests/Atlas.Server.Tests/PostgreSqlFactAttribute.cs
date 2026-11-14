using Xunit;

namespace Atlas.Server.Tests;

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        Skip = SkipReason(Environment.GetEnvironmentVariable("CI"), Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTION"));
    }

    /// <summary>
    /// Why a database test is skipped, or null when it runs. On CI (the CI variable is set) a missing connection fails the
    /// run instead: a renamed variable in the workflow must not turn every database test, the guards and the migration
    /// tests among them, into a green skip.
    /// </summary>
    internal static string? SkipReason(string? ci, string? connection)
    {
        if (!string.IsNullOrWhiteSpace(connection))
        {
            return null;
        }
        return string.IsNullOrEmpty(ci)
            ? "Set ATLAS_TEST_CONNECTION to a disposable PostgreSQL database."
            : throw new InvalidOperationException("ATLAS_TEST_CONNECTION is not set on CI (CI is set): the database tests would be skipped. Set it to the disposable PostgreSQL database of the job.");
    }
}
