using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>
/// The guard's replica of a document (<see cref="YjsDocumentTree"/>) against real Yjs: after every step of the corpus it must hold
/// the document Yjs holds (Fixtures/yjs-corpus/documents.json, recorded by packages/markdown/src/collab/yjsCorpusDocuments.test.ts),
/// and a journaled payload rolled back must leave it exactly as before.
/// </summary>
public sealed class YjsDocumentTreeTests
{
    private const long MaxBytes = 32L * 1024 * 1024;

    private static readonly Lazy<Dictionary<string, string>> Expected = new(() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(YjsCorpus.Root, "documents.json")))!);

    public static TheoryData<string> Scenarios => new(YjsCorpus.Manifest.Select(entry => entry.Scenario).Distinct());

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Every_corpus_step_yields_the_document_yjs_holds(string scenario)
    {
        var entries = YjsCorpus.Manifest.Where(entry => entry.Scenario == scenario).OrderBy(entry => entry.Seq).ToArray();
        var baseline = YjsCorpus.Read(entries[0].File);
        var running = YjsCorpus.Index(baseline);
        var compared = 0;
        foreach (var entry in entries)
        {
            if (!Expected.Value.TryGetValue(entry.File, out var expected)) continue; // pending or embed: no document
            YjsDocumentIndex index;
            if (entry.IsState) index = YjsCorpus.Index(YjsCorpus.Read(entry.File));
            else
            {
                index = scenario == "hostile" ? YjsCorpus.Index(baseline) : running;
                index.ApplyTrusted(YjsCorpus.Read(entry.File), MaxBytes);
            }
            Assert.True(expected == Hash(index.Tree!), $"{entry.File}: the replica differs from Yjs:\n{Canonical(index.Tree!)}");
            compared++;
        }
        Assert.True(compared > 0);
    }

    public static TheoryData<string> PerfSizes => new(new[] { "50k", "256k", "1m" }
        .Where(size => File.Exists(Path.Combine(YjsCorpus.Root, "perf", size, "state.bin"))));

    [Theory]
    [MemberData(nameof(PerfSizes))]
    public void Random_edits_of_a_large_document_follow_yjs_and_roll_back_exactly(string size)
    {
        var directory = Path.Combine(YjsCorpus.Root, "perf", size);
        var index = YjsCorpus.Index(File.ReadAllBytes(Path.Combine(directory, "state.bin")));
        var tree = index.Tree!;
        Assert.Equal(Expected.Value[$"perf/{size}/state.bin"], Hash(tree));
        foreach (var file in Directory.GetFiles(directory, "u*.bin").OrderBy(file => file, StringComparer.Ordinal))
        {
            var payload = File.ReadAllBytes(file);
            var parsed = YjsUpdateDecoder.Decode(payload, MaxBytes);
            var parents = YjsParentResolver.Resolve(parsed, index);
            // Applied, rolled back and applied again: the rollback must restore the very same document.
            var before = Canonical(tree);
            tree.Apply(payload, parsed, journal: true);
            tree.Rollback();
            Assert.Equal(before, Canonical(tree));
            tree.Apply(payload, parsed, journal: true);
            tree.Commit();
            index.Commit(payload, parsed, parents);
            Assert.True(Expected.Value[$"perf/{size}/{Path.GetFileName(file)}"] == Hash(tree), $"perf/{size}/{Path.GetFileName(file)}");
        }
    }

    internal static string Hash(YjsDocumentTree tree)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(tree)))).ToLowerInvariant()[..16];

    /// <summary>The canonical form of the editor test: {t, a, c} per element, {x: length, m: formats} per merged text run, as JSON.stringify writes it.</summary>
    internal static string Canonical(YjsDocumentTree tree)
    {
        var output = new StringBuilder();
        if (tree.Root("default") is { } root) Children(output, root);
        else output.Append("[]");
        return output.ToString();
    }

    private static void Children(StringBuilder output, YTreeType type)
    {
        output.Append('[');
        var first = true;
        for (var item = type.Start; item is not null; item = item.Right)
        {
            if (item.Deleted || item.Type is not { } child) continue;
            if (child.Kind == YTypeRef.XmlElement)
            {
                if (!first) output.Append(',');
                first = false;
                output.Append("{\"a\":");
                var attributes = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var (key, value) in child.Map ?? [])
                    if (!value.Deleted && value.Content == YContentKind.Any && value.Value is { } bytes && !Lib0AnyJson.IsUndefined(bytes))
                        attributes[key] = Lib0AnyJson.ToJson(bytes);
                Object(output, attributes);
                output.Append(",\"c\":");
                Children(output, child);
                output.Append(",\"t\":");
                Text(output, child.Name!);
                output.Append('}');
            }
            else if (child.Kind == YTypeRef.XmlText)
            {
                foreach (var (length, marks) in Runs(child))
                {
                    if (!first) output.Append(',');
                    first = false;
                    output.Append("{\"m\":").Append(marks).Append(",\"x\":").Append(length.ToString(CultureInfo.InvariantCulture)).Append('}');
                }
            }
        }
        output.Append(']');
    }

    private static List<(long Length, string Marks)> Runs(YTreeType text)
    {
        var runs = new List<(long Length, string Marks)>();
        var formats = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var item = text.Start; item is not null; item = item.Right)
        {
            if (item.Deleted) continue;
            if (item.Content == YContentKind.Format)
            {
                using var value = JsonDocument.Parse(item.Value);
                if (value.RootElement.ValueKind == JsonValueKind.Null) formats.Remove(item.Key!);
                else formats[item.Key!] = value.RootElement.Clone();
                continue;
            }
            if (item.Content != YContentKind.String) continue;
            var marks = new StringBuilder();
            Object(marks, formats);
            if (runs.Count > 0 && runs[^1].Marks == marks.ToString()) runs[^1] = (runs[^1].Length + item.Length, runs[^1].Marks);
            else runs.Add((item.Length, marks.ToString()));
        }
        return runs;
    }

    private static void Object(StringBuilder output, IEnumerable<KeyValuePair<string, JsonElement>> properties)
    {
        output.Append('{');
        var first = true;
        foreach (var (key, value) in properties)
        {
            if (!first) output.Append(',');
            first = false;
            Text(output, key);
            output.Append(':');
            Value(output, value);
        }
        output.Append('}');
    }

    private static void Value(StringBuilder output, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                Object(output, value.EnumerateObject().Select(property => KeyValuePair.Create(property.Name, property.Value)).OrderBy(pair => pair.Key, StringComparer.Ordinal));
                break;
            case JsonValueKind.Array:
                output.Append('[').AppendJoin(',', value.EnumerateArray().Select(item => { var part = new StringBuilder(); Value(part, item); return part.ToString(); })).Append(']');
                break;
            case JsonValueKind.String:
                Text(output, value.GetString()!);
                break;
            default:
                output.Append(value.GetRawText());
                break;
        }
    }

    /// <summary>JSON.stringify of a string.</summary>
    private static void Text(StringBuilder output, string text)
    {
        output.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (c < ' ') output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else output.Append(c);
                    break;
            }
        }
        output.Append('"');
    }
}
