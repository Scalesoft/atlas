using Npgsql;

namespace Atlas.Server.Tests;

internal static class PostgreSqlTestConnection
{
    public static string Read(string variable)
    {
        // ATLAS_TEST_DATABASE (search and OIDC tests) defaults to the one server every other test uses.
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value) && variable != "ATLAS_TEST_CONNECTION")
        {
            variable = "ATLAS_TEST_CONNECTION";
        }
        // Admin connections and fixtures that build their own schema are one-use strings; keep them out of pools
        // instead of retaining idle backends. Disposable databases opt back in and drop their pool (TestDatabases).
        return new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(variable))
        {
            Pooling = false
        }.ConnectionString;
    }
}
