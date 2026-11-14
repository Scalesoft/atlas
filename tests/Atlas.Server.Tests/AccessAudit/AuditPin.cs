using Npgsql;

namespace Atlas.Server.Tests.AccessAudit;

/// <summary>
/// The audit an operation writes, as text to pin (3.6, no logic in the database): one line per AccessAuditEvent after a mark, its action,
/// the actor's username ('-' for none) and the one target column it names; share events (PageAccessEvent share.*) and revision rows in
/// their own reads. The pins were taken while the database triggers still wrote these rows and must stay green now that C# writes them.
/// </summary>
internal static class AuditPin
{
    public static async Task<long> MarkAsync(string connectionString)
        => (long)(await ScalarAsync(connectionString, """SELECT COALESCE(max("Id"),0) FROM "AccessAuditEvent" """))!;

    public static async Task<string[]> EventsAsync(string connectionString, long after = 0)
    {
        var text = await ScalarAsync(connectionString, $"""
            SELECT string_agg(e."Action" || ' ' || COALESCE(u."Username",'-') || ' ' ||
                (SELECT string_agg(key, ',') FROM jsonb_each(to_jsonb(e) - 'Id' - 'ActorId' - 'OccurredAt' - 'Action') WHERE jsonb_typeof(value) <> 'null'),
                E'\n' ORDER BY e."Id")
            FROM "AccessAuditEvent" e LEFT JOIN "UserAccount" u ON u."Id" = e."ActorId" WHERE e."Id" > {after}
            """);
        return text is string lines ? lines.Split('\n') : [];
    }

    /// <summary>The share events of PageAccessEvent: action and actor ('-' for none), in order.</summary>
    public static async Task<string[]> ShareEventsAsync(string connectionString)
    {
        var text = await ScalarAsync(connectionString, """
            SELECT string_agg(e."Action" || ' ' || COALESCE(u."Username",'-'), E'\n' ORDER BY e."Id")
            FROM "PageAccessEvent" e LEFT JOIN "UserAccount" u ON u."Id" = e."ActorId" WHERE e."Action" LIKE 'share.%'
            """);
        return text is string lines ? lines.Split('\n') : [];
    }

    /// <summary>The rows of a revision table in order: who changed it ('-' for none) and whether it is closed, plus the given columns
    /// (a name, or an expression over the row r).</summary>
    public static async Task<string[]> RevisionsAsync(string connectionString, string table, string changedBy = "ChangedBy", params string[] columns)
    {
        var values = string.Concat(columns.Select(column => $" || ' ' || COALESCE(({(column.Contains(' ') ? column : $"r.\"{column}\"")})::text,'-')"));
        var text = await ScalarAsync(connectionString, $"""
            SELECT string_agg(COALESCE(u."Username",'-') || CASE WHEN r."ActiveTo" IS NULL THEN ' open' ELSE ' closed' END{values},
                E'\n' ORDER BY r."ActiveFrom", r."Id")
            FROM "{table}" r LEFT JOIN "UserAccount" u ON u."Id" = r."{changedBy}"
            """);
        return text is string lines ? lines.Split('\n') : [];
    }

    /// <summary>The rows of a query that selects a line and an order (line, n).</summary>
    public static async Task<string[]> LinesAsync(string connectionString, string select)
    {
        var text = await ScalarAsync(connectionString, $"SELECT string_agg(q.line, E'\\n' ORDER BY q.n) FROM ({select}) q(line, n)");
        return text is string lines ? lines.Split('\n') : [];
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        // Taking a new pin: ATLAS_PIN_DUMP=<file> appends every read in full (xUnit truncates a differing collection).
        if (Environment.GetEnvironmentVariable("ATLAS_PIN_DUMP") is { Length: > 0 } dump)
        {
            File.AppendAllText(dump, $"---\n{value}\n");
        }
        return value is DBNull ? null : value;
    }
}
