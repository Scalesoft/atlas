using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Tests.Platform;
using Npgsql;
using Xunit;
using static Atlas.Server.Tests.Boards.BoardsFixture;

namespace Atlas.Server.Tests.Boards;

/// <summary>A board batch rewrites the board's search document (and asks for the reference pass) only when it changes what they
/// read: the title, an item's text, what search reads of its props, or its presence; not a colour, a move or a size.</summary>
public sealed class BoardBatchSearchRefreshTests
{
    private static BoardChange Paragraph(Guid id, string text)
    {
        return new BoardChange("create", Item: new BoardItemInput(id, "block", 0, 0, 200, 100, 1, Props: Block(text)));
    }

    private static JsonObject Block(string text)
    {
        return JsonNode.Parse($$$"""{"block":{"type":"paragraph","content":[{"type":"text","text":"{{{text}}}"}]}}""")!.AsObject();
    }

    private static async Task<(string Title, string Body)> DocumentAsync(BoardsFixture f, Guid board)
    {
        await using var connection = new NpgsqlConnection(f.Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""SELECT "Title","Body" FROM "BoardSearchDocument" WHERE "BoardId"='{board}'""", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<IReadOnlyList<string>> RecordAsync(BoardsFixture f, Guid board, params BoardChange[] changes)
    {
        using var recording = SqlRecording.Start();
        await f.ApplyAsync(f.Writer, board, changes);
        return recording.Statements;
    }

    private static bool Refreshed(IReadOnlyList<string> statements) => statements.Any(sql => sql.Contains("\"BoardSearchDocument\"", StringComparison.Ordinal));

    [PostgreSqlFact]
    public async Task A_batch_rewrites_the_search_document_only_when_it_changes_what_is_indexed()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var board = await f.CreateBoardAsync(title: "Plán");
        Guid note = Guid.NewGuid(), block = Guid.NewGuid(), other = Guid.NewGuid();
        await f.ApplyAsync(f.Writer, board, Note(note, "alfa"), Paragraph(block, "bravo"));

        // Not indexed: colour, geometry, order, a text set to what it already is.
        Assert.False(Refreshed(await RecordAsync(f, board, new BoardChange("set", ItemId: note, Color: "blue"))));
        Assert.False(Refreshed(await RecordAsync(f, board, new BoardChange("move", ItemId: note, X: 40, Y: 50, Width: 200, Height: 100))));
        Assert.False(Refreshed(await RecordAsync(f, board, new BoardChange("reorder", ItemId: note, Z: -1))));
        Assert.False(Refreshed(await RecordAsync(f, board, new BoardChange("set", ItemId: note, Text: "alfa"))));
        Assert.Equal(("Plán", "alfa\n\nbravo"), await DocumentAsync(f, board));

        // Indexed: a new text, a block's props, the title, an item's creation, deletion and restoration.
        Assert.True(Refreshed(await RecordAsync(f, board, new BoardChange("set", ItemId: note, Text: "charlie"))));
        Assert.Equal("charlie\n\nbravo", (await DocumentAsync(f, board)).Body);
        Assert.True(Refreshed(await RecordAsync(f, board, new BoardChange("set", ItemId: block, Props: Block("delta")))));
        Assert.Equal("charlie\n\ndelta", (await DocumentAsync(f, board)).Body);
        Assert.True(Refreshed(await RecordAsync(f, board, new BoardChange("rename", Title: "Nový"))));
        Assert.Equal("Nový", (await DocumentAsync(f, board)).Title);
        Assert.True(Refreshed(await RecordAsync(f, board, new BoardChange("create", Item: new BoardItemInput(other, "note", 0, 0, 160, 120, 2, Text: "echo")))));
        Assert.Equal("charlie\n\ndelta\n\necho", (await DocumentAsync(f, board)).Body);
        Assert.True(Refreshed(await RecordAsync(f, board, new BoardChange("delete", ItemId: other))));
        Assert.Equal("charlie\n\ndelta", (await DocumentAsync(f, board)).Body);
        Assert.True(Refreshed(await RecordAsync(f, board, new BoardChange("restore", ItemId: other))));
        Assert.Equal("charlie\n\ndelta\n\necho", (await DocumentAsync(f, board)).Body);
    }

    [PostgreSqlFact]
    public async Task Board_batches_send_exactly_these_statements_once_warm()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var board = await f.CreateBoardAsync();
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();
        await f.ApplyAsync(f.Writer, board, ids.Select(id => Note(id, "text " + id.ToString("N")[..6])).ToArray());
        // Warm-up: each kind of batch once before it is counted.
        await f.ApplyAsync(f.Writer, board, new BoardChange("set", ItemId: ids[0], Color: "green"));
        await f.ApplyAsync(f.Writer, board, new BoardChange("set", ItemId: ids[0], Text: "warm"));
        await f.ApplyAsync(f.Writer, board, new BoardChange("move", ItemId: ids[0], X: 1, Y: 1, Width: 160, Height: 120));

        BoardChange Move(Guid id, double x) => new("move", ItemId: id, X: x, Y: 2, Width: 160, Height: 120);
        (string Name, int Expected, IReadOnlyList<string> Sent)[] counted =
        [
            // 18: no search refresh and no size budget (a colour changes neither); a text set is 20.
            ("colour", 18, await RecordAsync(f, board, new BoardChange("set", ItemId: ids[1], Color: "blue"))),
            ("text", 20, await RecordAsync(f, board, new BoardChange("set", ItemId: ids[1], Text: "changed"))),
            ("move of 1", 17, await RecordAsync(f, board, Move(ids[1], 5))),
            // 66: one UPDATE per item (NHibernate batching is off; adonet.batch_size breaks the jsonb parameters).
            ("move of 50", 66, await RecordAsync(f, board, ids.Select(id => Move(id, 9)).ToArray())),
        ];
        static string Line(string sql) { var one = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); return one.Length > 90 ? one[..90] : one; }
        var report = string.Join(Environment.NewLine + Environment.NewLine, counted.Select(x =>
            $"{x.Name}: {x.Sent.Count} statements, expected {x.Expected}{Environment.NewLine}"
            + string.Join(Environment.NewLine, x.Sent.Take(x.Sent.Count > 30 ? 0 : 40).Select((sql, index) => $"  {index + 1}. {Line(sql)}"))));
        Assert.True(counted.All(x => x.Sent.Count == x.Expected), report);
    }
}
