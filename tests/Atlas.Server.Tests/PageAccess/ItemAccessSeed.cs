using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PageAccess;

/// <summary>Raw seeding for ResourceGrant, which has only a read-only entity until the management slice exists.</summary>
internal static class ItemAccessSeed
{
    public static async Task<Guid> PageAsync(AtlasTestHost host, Guid spaceId, UserAccount owner, bool ownAccess = false)
    {
        var page = new Page { Id = Guid.NewGuid(), SpaceId = spaceId, OwnerId = owner.Id, OwnAccess = ownAccess };
        // As the application creates a page: with the first revision of its access mode (no actor).
        await host.SeedAsync(async session =>
        {
            await session.SaveAsync(page);
            await PageAccessModeHistory.RecordAsync(session, page, null, CancellationToken.None);
        });
        return page.Id;
    }

    public static async Task<Guid> GrantAsync(NpgsqlConnection connection, Guid pageId, Guid? userId, Guid? groupId, int role,
        string startAt = "NULL", string expiresAt = "NULL")
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(connection, $"""
            INSERT INTO "ResourceGrant"("Id","PageId","UserId","GroupId","Role","StartAt","ExpiresAt")
            VALUES ('{id}','{pageId}',{Literal(userId)},{Literal(groupId)},{role},{startAt},{expiresAt})
            """);
        return id;
    }

    public static async Task<NpgsqlConnection> OpenAsync(AtlasTestHost host)
    {
        var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public static async Task<object?> ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    public static async Task RejectedAsync(NpgsqlConnection connection, string sqlState, string sql)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, sql));
        Assert.Equal(sqlState, error.SqlState);
    }

    private static string Literal(Guid? id) => id is { } value ? $"'{value}'" : "NULL";
}
