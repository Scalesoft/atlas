using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Atlas.Server.Tests.Search.Parity;

/// <summary>
/// The search functions before Atlas 3.6 moved them into C# query text (legacy-search-functions.sql, a pg_dump of feature/atlas-3.6
/// 05ec2d7e6), for parity tests: <see cref="InstallAsync"/> creates them in a fixture's own schema, <see cref="AssertSameAsync"/> runs a
/// function and the C# text that replaced it with the same arguments in one transaction and compares the rows: same ids, same order,
/// equal values, floats within <see cref="Epsilon"/>.
/// </summary>
internal static class LegacySearch
{
    public const double Epsilon = 1e-5;

    /// <summary>An argument as production binds it; <paramref name="Cast"/> is the type the C# text casts it to (a vector or an array
    /// travels as text), so the legacy call casts it the same way.</summary>
    public sealed record Arg(string Name, object? Value, NpgsqlDbType Type, string? Cast = null);

    public static async Task InstallAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(ScriptPath()), connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The call of <paramref name="function"/> with <paramref name="args"/> in their order.</summary>
    public static string Call(string function, IEnumerable<Arg> args)
        => $"SELECT * FROM {function}({string.Join(",", args.Select(a => a.Cast is null ? ":" + a.Name : $"CAST(:{a.Name} AS {a.Cast})"))})";

    /// <summary>Runs <paramref name="legacy"/> and <paramref name="current"/> in one transaction after <paramref name="settings"/> (the
    /// caller's set_config) and asserts the same rows; returns the rows with a value.</summary>
    public static async Task<int> AssertSameAsync(string connectionString, string settings, string legacy, string current, IReadOnlyList<Arg> args,
        string because)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (settings.Length > 0)
        {
            await using var prepare = new NpgsqlCommand(settings, connection, transaction);
            await prepare.ExecuteNonQueryAsync();
        }
        var expected = await RowsAsync(connection, transaction, legacy, args);
        var actual = await RowsAsync(connection, transaction, current, args);
        await transaction.RollbackAsync();
        Assert.True(expected.Count == actual.Count, $"{because}: {expected.Count} legacy rows, {actual.Count} now.\n{Show(expected)}\n---\n{Show(actual)}");
        for (var row = 0; row < expected.Count; row++)
        {
            Assert.True(expected[row].Length == actual[row].Length, $"{because}: row {row} has {expected[row].Length} columns, now {actual[row].Length}.");
            for (var column = 0; column < expected[row].Length; column++)
            {
                Assert.True(Same(expected[row][column], actual[row][column]),
                    $"{because}: row {row}, column {column}: legacy {Text(expected[row][column])}, now {Text(actual[row][column])}.\n{Show(expected)}\n---\n{Show(actual)}");
            }
        }
        return expected.Count(row => row.Any(value => value is not null));
    }

    private static async Task<List<object?[]>> RowsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, IReadOnlyList<Arg> args)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter(arg.Name, arg.Type) { Value = arg.Value ?? DBNull.Value });
        }
        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }

    private static bool Same(object? expected, object? actual) => (expected, actual) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (float a, float b) => Math.Abs(a - b) <= Epsilon,
        (double a, double b) => Math.Abs(a - b) <= Epsilon,
        (Array a, Array b) => a.Length == b.Length && a.Cast<object?>().Zip(b.Cast<object?>()).All(pair => Same(pair.First, pair.Second)),
        _ => expected.Equals(actual),
    };

    private static string Text(object? value) => value switch
    {
        null => "null",
        Array array => "[" + string.Join(",", array.Cast<object?>().Select(Text)) + "]",
        _ => value.ToString() ?? "",
    };

    private static string Show(List<object?[]> rows) => string.Join("\n", rows.Select(row => string.Join(" | ", row.Select(value => Text(value) is var text && text.Length > 80 ? text[..80] + "…" : Text(value)))));

    private static string ScriptPath([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "legacy-search-functions.sql");
}
