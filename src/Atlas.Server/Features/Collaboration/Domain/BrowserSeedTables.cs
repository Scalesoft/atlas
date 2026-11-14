using System.Text.Json.Nodes;
using static Atlas.Server.Features.Collaboration.Domain.SeedNodes;

namespace Atlas.Server.Features.Collaboration.Domain;

/// <summary>
/// prosemirror-tables 1.8.5's fixTables (tableEditing's appendTransaction, as MarkdownTable runs it on a local change) over the stored JSON:
/// TableMap's computeMap/findWidth/findBadColWidths find a table's problems, fixTable repairs them (colwidth mismatch, collision, overlong
/// rowspan, missing cells, zero size).
/// </summary>
internal static class BrowserSeedTables
{
    /// <summary>Every table, nested ones too. A fix that added cells is checked again, as the editor's next round does once the chart
    /// references plugin has given the new cells their ids (the table changed, so fixTables looks at it again).</summary>
    public static void Apply(JsonObject doc)
    {
        var tables = new List<(JsonNode Table, JsonArray Parent)>();
        Collect(doc, tables);
        foreach (var (table, parent) in tables)
        {
            while (FixTable(table, parent))
            {
            }
        }
    }

    private static void Collect(JsonNode node, List<(JsonNode, JsonArray)> tables)
    {
        foreach (var child in Content(node) ?? [])
        {
            if (Type(child) == "table") tables.Add((child!, Content(node)!));
            Collect(child!, tables);
        }
    }

    private sealed record Problem(string Type, int Pos = 0, int Row = 0, int N = 0, List<JsonNode?>? Colwidth = null);

    private sealed record Map(int Width, int Height, int[] Cells, List<Problem> Problems);

    private static JsonObject AttrsOf(JsonNode cell) => cell["attrs"] as JsonObject ?? (JsonObject)(cell["attrs"] = new JsonObject());

    /// <summary>A numeric attribute as JS arithmetic reads it: its default when absent, 0 when null.</summary>
    private static int Number(JsonNode cell, string name, int fallback)
        => cell["attrs"] is not JsonObject attrs || !attrs.TryGetPropertyValue(name, out var value) ? fallback
            : (int)(JsNumber(value) ?? 0);

    private static List<JsonNode?>? Colwidth(JsonNode cell) => cell["attrs"]?["colwidth"] is JsonArray widths ? [.. widths.Select(width => width?.DeepClone())] : null;

    /// <summary>A colwidth entry as JS truthiness and number comparison read it: 0 for none.</summary>
    private static double Width(List<JsonNode?>? widths, int index)
        => widths is not null && index < widths.Count ? JsNumber(widths[index]) ?? 0 : 0;

    // findWidth
    private static int FindWidth(JsonArray rows)
    {
        var width = -1;
        var hasRowSpan = false;
        for (var row = 0; row < rows.Count; row++)
        {
            var rowWidth = 0;
            if (hasRowSpan)
            {
                for (var j = 0; j < row; j++)
                {
                    foreach (var cell in Content(rows[j]) ?? [])
                    {
                        if (j + Number(cell!, "rowspan", 1) > row) rowWidth += Number(cell!, "colspan", 1);
                    }
                }
            }
            foreach (var cell in Content(rows[row]) ?? [])
            {
                rowWidth += Number(cell!, "colspan", 1);
                if (Number(cell!, "rowspan", 1) > 1) hasRowSpan = true;
            }
            width = width == -1 ? rowWidth : Math.Max(width, rowWidth);
        }
        return width;
    }

    /// <summary>computeMap; <paramref name="cellAt"/> is table.nodeAt for the cells' offsets.</summary>
    private static Map ComputeMap(JsonNode table, Dictionary<int, JsonNode> cellAt)
    {
        var rows = Content(table) ?? [];
        int width = FindWidth(rows), height = rows.Count;
        var map = new int[Math.Max(0, width * height)];
        var mapPos = 0;
        var problems = new List<Problem>();
        var colWidths = new Dictionary<int, double>();
        for (int row = 0, pos = 0; row < height; row++)
        {
            var cells = Content(rows[row]) ?? [];
            pos++;
            for (var i = 0; ; i++)
            {
                while (mapPos < map.Length && map[mapPos] != 0) mapPos++;
                if (i == cells.Count) break;
                var cell = cells[i]!;
                cellAt[pos] = cell;
                int colspan = Number(cell, "colspan", 1), rowspan = Number(cell, "rowspan", 1);
                var colwidth = Colwidth(cell);
                for (var h = 0; h < rowspan; h++)
                {
                    if (h + row >= height)
                    {
                        problems.Add(new Problem("overlong_rowspan", Pos: pos, N: rowspan - h));
                        break;
                    }
                    var start = mapPos + h * width;
                    for (var w = 0; w < colspan; w++)
                    {
                        // map[start + w] == 0: beyond the map it is undefined, a collision too
                        if (start + w < map.Length && map[start + w] == 0) map[start + w] = pos;
                        else problems.Add(new Problem("collision", Pos: pos, Row: row, N: colspan - w));
                        var colW = Width(colwidth, w);
                        if (colW != 0)
                        {
                            var widthIndex = (start + w) % width * 2;
                            if (!colWidths.TryGetValue(widthIndex, out var prev) || (prev != colW && colWidths[widthIndex + 1] == 1))
                            {
                                colWidths[widthIndex] = colW;
                                colWidths[widthIndex + 1] = 1;
                            }
                            else if (prev == colW)
                            {
                                colWidths[widthIndex + 1]++;
                            }
                        }
                    }
                }
                mapPos += colspan;
                pos += NodeSize(cell);
            }
            var expectedPos = (row + 1) * width;
            var missing = 0;
            while (mapPos < expectedPos)
            {
                if (map[mapPos++] == 0) missing++;
            }
            if (missing > 0) problems.Add(new Problem("missing", Row: row, N: missing));
            pos++;
        }
        if (width == 0 || height == 0) problems.Add(new Problem("zero_sized"));
        var result = new Map(width, height, map, problems);
        if (colWidths.Any(entry => entry.Key % 2 == 0 && colWidths[entry.Key + 1] < height)) FindBadColWidths(result, colWidths, cellAt);
        return result;
    }

    // findBadColWidths
    private static void FindBadColWidths(Map map, Dictionary<int, double> colWidths, Dictionary<int, JsonNode> cellAt)
    {
        var seen = new HashSet<int>();
        for (var i = 0; i < map.Cells.Length; i++)
        {
            var pos = map.Cells[i];
            if (!seen.Add(pos)) continue;
            // An unfilled slot (0) is the first row, which has no colspan: nothing to compare.
            if (!cellAt.TryGetValue(pos, out var cell)) continue;
            List<JsonNode?>? updated = null;
            var colwidth = Colwidth(cell);
            for (var j = 0; j < Number(cell, "colspan", 1); j++)
            {
                if (colWidths.TryGetValue((i + j) % map.Width * 2, out var colWidth) && (colwidth is null || Width(colwidth, j) != colWidth))
                {
                    updated ??= FreshColWidth(cell);
                    while (updated.Count <= j) updated.Add(null);
                    updated[j] = JsonValue.Create(colWidth);
                }
            }
            if (updated is not null) map.Problems.Insert(0, new Problem("colwidth mismatch", Pos: pos, Colwidth: updated));
        }
    }

    // freshColWidth
    private static List<JsonNode?> FreshColWidth(JsonNode cell)
        => Colwidth(cell) ?? [.. Enumerable.Range(0, Math.Max(0, Number(cell, "colspan", 1))).Select(_ => (JsonNode?)JsonValue.Create(0))];

    // removeColSpan
    private static JsonObject RemoveColSpan(JsonNode cell, int pos, int n)
    {
        var attrs = (JsonObject)AttrsOf(cell).DeepClone();
        attrs["colspan"] = Number(cell, "colspan", 1) - n;
        if (Colwidth(cell) is { } widths)
        {
            if (pos < widths.Count) widths.RemoveRange(pos, Math.Min(n, widths.Count - pos));
            attrs["colwidth"] = widths.Any(width => JsNumber(width) > 0) ? new JsonArray([.. widths]) : null;
        }
        return attrs;
    }

    /// <summary>fixTable: whether it added cells. Every attribute fix starts from the cell as the table had it (setNodeMarkup of
    /// table.nodeAt), so a later fix of the same cell replaces an earlier one.</summary>
    private static bool FixTable(JsonNode table, JsonArray parent)
    {
        var cellAt = new Dictionary<int, JsonNode>();
        var map = ComputeMap(table, cellAt);
        if (map.Problems.Count == 0) return false;
        var mustAdd = new int[map.Height];
        var markup = new Dictionary<JsonNode, JsonObject>(ReferenceEqualityComparer.Instance);
        var deleted = false;
        foreach (var problem in map.Problems)
        {
            switch (problem.Type)
            {
                case "collision" when cellAt.TryGetValue(problem.Pos, out var cell):
                    for (var j = 0; j < Number(cell, "rowspan", 1); j++)
                    {
                        // mustAdd beyond the table's rows is NaN in JS, which no row reads
                        if (problem.Row + j < mustAdd.Length) mustAdd[problem.Row + j] += problem.N;
                    }
                    markup[cell] = RemoveColSpan(cell, Number(cell, "colspan", 1) - problem.N, problem.N);
                    break;
                case "missing":
                    mustAdd[problem.Row] += problem.N;
                    break;
                case "overlong_rowspan" when cellAt.TryGetValue(problem.Pos, out var cell):
                    var rowspan = (JsonObject)AttrsOf(cell).DeepClone();
                    rowspan["rowspan"] = Number(cell, "rowspan", 1) - problem.N;
                    markup[cell] = rowspan;
                    break;
                case "colwidth mismatch" when cellAt.TryGetValue(problem.Pos, out var cell):
                    var colwidth = (JsonObject)AttrsOf(cell).DeepClone();
                    colwidth["colwidth"] = new JsonArray([.. problem.Colwidth!]);
                    markup[cell] = colwidth;
                    break;
                case "zero_sized":
                    deleted = true;
                    break;
            }
        }
        foreach (var (cell, attrs) in markup) cell["attrs"] = attrs;
        if (deleted)
        {
            // shortcut: tr.delete of a table that is its container's only block would let ProseMirror's replace fit the container; a table
            // with no cell at all is not written by the editor, so it is removed as it stands.
            parent.Remove(table);
            return false;
        }
        int? first = null, last = null;
        for (var i = 0; i < mustAdd.Length; i++)
        {
            if (mustAdd[i] == 0) continue;
            first ??= i;
            last = i;
        }
        var rows = Content(table)!;
        var added = false;
        for (var i = 0; i < map.Height; i++)
        {
            if (mustAdd[i] <= 0) continue;
            var cells = Content(rows[i]) ?? (JsonArray)(rows[i]!["content"] = new JsonArray());
            var type = cells.Count > 0 && Type(cells[0]) == "tableHeader" ? "tableHeader" : "tableCell";
            // createAndFill: an empty cell holds one empty paragraph (block+)
            var nodes = Enumerable.Range(0, mustAdd[i]).Select(_ => new JsonObject { ["type"] = type, ["content"] = new JsonArray(new JsonObject { ["type"] = "paragraph" }) }).ToArray();
            if ((i == 0 || first == i - 1) && last == i)
            {
                for (var j = nodes.Length - 1; j >= 0; j--) cells.Insert(0, nodes[j]);
            }
            else
            {
                foreach (var node in nodes) cells.Add(node);
            }
            added = true;
        }
        return added;
    }
}
