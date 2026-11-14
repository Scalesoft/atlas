using NHibernate;
using Npgsql;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Persistence;

internal static class DatabaseStatistics
{
    private const string AccessTables = "ANALYZE \"UserAccount\", \"Space\", \"SpaceGrant\", \"GroupMember\"";
    private const string ImportedContentTables =
        "ANALYZE \"Page\", \"PageRevision\", \"SearchDocument\", \"WorkingSearchDocument\", \"Space\", \"UserAccount\", \"SpaceGrant\", \"GroupMember\"";

    public static async Task RefreshAccessAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(AccessTables, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static Task RefreshAccessAsync(ISession session, CancellationToken ct)
        => session.CreateSQLQuery(AccessTables).ExecuteUpdateAsync(ct);

    public static Task RefreshImportedContentAsync(ISession session, CancellationToken ct)
        => session.CreateSQLQuery(ImportedContentTables).ExecuteUpdateAsync(ct);
}
