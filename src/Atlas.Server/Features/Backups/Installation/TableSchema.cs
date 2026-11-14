using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Backups.Installation;

internal sealed record TableColumn(string Name, string Type, bool Nullable, bool Generated, bool Identity);

internal sealed record TableReference(string Table, IReadOnlyList<string> Columns, string Target, bool Deferrable);

internal sealed record TableInfo(string Name, IReadOnlyList<string> Key, IReadOnlyDictionary<string, TableColumn> Columns,
    IReadOnlyList<TableReference> References);

/// <summary>The tables of the current schema as PostgreSQL describes them: the generic export and restore follow the real schema,
/// so a new column is backed up without a change here.</summary>
internal static class TableSchema
{
    // Every column has its own alias: NHibernate maps the results of native SQL by column name.
    private const string InSchema = "c.relnamespace = (SELECT n.oid FROM pg_namespace n WHERE n.nspname = current_schema()) AND c.relkind IN ('r', 'p')";

    public static async Task<IReadOnlyDictionary<string, TableInfo>> ReadAsync(ISession session, CancellationToken ct)
    {
        var columns = await session.CreateSQLQuery(
                "SELECT c.relname AS table_name, a.attname AS column_name, format_type(a.atttypid, a.atttypmod) AS column_type, NOT a.attnotnull AS nullable, "
                + "a.attgenerated <> '' AS generated, a.attidentity <> '' AS identity "
                + "FROM pg_class c JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped WHERE " + InSchema
                + " ORDER BY c.relname, a.attnum")
            .ListAsync<object[]>(ct);
        var keys = await session.CreateSQLQuery(
                "SELECT c.relname AS table_name, a.attname AS column_name FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid "
                + "CROSS JOIN LATERAL unnest(k.conkey) WITH ORDINALITY u(attnum, ord) JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = u.attnum "
                + "WHERE k.contype = 'p' AND " + InSchema + " ORDER BY c.relname, u.ord")
            .ListAsync<object[]>(ct);
        var references = await session.CreateSQLQuery(
                "SELECT c.relname AS table_name, t.relname AS target_name, k.condeferrable AS deferrable, (SELECT string_agg(a.attname, ',' ORDER BY u.ord) "
                + "FROM unnest(k.conkey) WITH ORDINALITY u(attnum, ord) JOIN pg_attribute a ON a.attrelid = k.conrelid AND a.attnum = u.attnum) AS column_names "
                + "FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid JOIN pg_class t ON t.oid = k.confrelid WHERE k.contype = 'f' AND " + InSchema
                + " ORDER BY c.relname, k.conname")
            .ListAsync<object[]>(ct);
        return columns.GroupBy(x => (string)x[0]).ToDictionary(table => table.Key, table => new TableInfo(table.Key,
            keys.Where(x => (string)x[0] == table.Key).Select(x => (string)x[1]).ToList(),
            table.ToDictionary(x => (string)x[1], x => new TableColumn((string)x[1], (string)x[2], (bool)x[3], (bool)x[4], (bool)x[5]), StringComparer.Ordinal),
            references.Where(x => (string)x[0] == table.Key).Select(x => new TableReference(table.Key, ((string)x[3]).Split(','), (string)x[1], (bool)x[2])).ToList()),
            StringComparer.Ordinal);
    }

    /// <summary>The tables in an order in which every non-deferrable reference between them points backwards.</summary>
    public static IReadOnlyList<string> Order(IEnumerable<string> tables, IReadOnlyDictionary<string, TableInfo> schema)
    {
        var pending = tables.Distinct(StringComparer.Ordinal).ToList();
        var ordered = new List<string>();
        while (pending.Count > 0)
        {
            var next = pending.FirstOrDefault(table => schema[table].References.All(r => r.Deferrable || r.Target == table
                || !pending.Contains(r.Target, StringComparer.Ordinal)))
                ?? throw new InvalidOperationException("Tables reference each other in a cycle: " + string.Join(", ", pending));
            ordered.Add(next);
            pending.Remove(next);
        }
        return ordered;
    }

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
