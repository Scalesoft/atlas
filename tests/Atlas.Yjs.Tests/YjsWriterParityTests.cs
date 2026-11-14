using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Atlas.Markdown.Sync;
using Xunit.Abstractions;

namespace Atlas.Yjs.Tests;

/// <summary>
/// <see cref="YjsDocumentWriter"/> (Atlas.Yjs's port of prosemirrorToYDoc and updateYFragment) against the live-write worker, on the
/// cases src/Atlas.Web/src/server/yjsWriterFixtures.test.ts records with fixed client ids (Fixtures/yjs-writer/cases.json.gz): the
/// seed's state and every write's update byte for byte, and the documents they and a concurrent web edit leave, in either order.
/// </summary>
public class YjsWriterParityTests(ITestOutputHelper output)
{
    public static TheoryData<string> Seeds => new(WriterCorpus.Cases.Where(entry => entry.Kind == "seed").Select(entry => entry.Name));
    public static TheoryData<string> Writes => new(WriterCorpus.Cases.Where(entry => entry.Kind == "write").Select(entry => entry.Name));
    public static TheoryData<string> Concurrent => new(WriterCorpus.Cases.Where(entry => entry.Kind == "concurrent").Select(entry => entry.Name));

    [Theory]
    [MemberData(nameof(Seeds))]
    public void A_seed_is_the_state_prosemirrorToYDoc_writes(string name)
    {
        var entry = WriterCorpus.Get(name);
        var state = WriterCorpus.Writer.Seed(entry.Json.GetProperty("normalized"), entry.ClientId);
        Assert.Equal(Convert.FromBase64String(entry.Json.GetProperty("normalizedState").GetString()!), state);
        var normalized = WriterCorpus.Stringify(entry.Json.GetProperty("normalized"));
        Assert.Equal(normalized, WriterCorpus.Materializer.Materialize(state));
        // The seed of the stored page reads back as the normalized document too (it may differ in null attributes, see the generator).
        Assert.Equal(normalized, WriterCorpus.Materializer.Materialize(Convert.FromBase64String(entry.Json.GetProperty("state").GetString()!)));
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public void A_live_write_is_the_update_updateYFragment_writes(string name)
    {
        var entry = WriterCorpus.Get(name);
        var payloads = WriterCorpus.Payloads(entry);
        var written = WriterCorpus.Writer.Write(payloads[0], payloads.Skip(1).Select(payload => (ReadOnlyMemory<byte>)payload), entry.Json.GetProperty("target"), entry.ClientId);
        var expected = Convert.FromBase64String(entry.Json.GetProperty("update").GetString()!);
        Assert.Equal(WriterCorpus.Materialize([.. payloads, expected]), WriterCorpus.Materialize([.. payloads, written.Update]));
        Assert.Equal(WriterCorpus.Stringify(entry.Json.GetProperty("result")), WriterCorpus.Materializer.Materialize(written.State));
        Assert.Equal(expected, written.Update);
    }

    [Theory]
    [MemberData(nameof(Concurrent))]
    public void A_live_write_merges_with_a_concurrent_edit_in_either_order(string name)
    {
        var entry = WriterCorpus.Get(name);
        var payloads = WriterCorpus.Payloads(entry);
        var web = Convert.FromBase64String(entry.Json.GetProperty("web").GetString()!);
        var written = WriterCorpus.Writer.Write(payloads[0], payloads.Skip(1).Select(payload => (ReadOnlyMemory<byte>)payload), entry.Json.GetProperty("target"), entry.ClientId);
        var result = WriterCorpus.Stringify(entry.Json.GetProperty("result"));
        Assert.Equal(result, WriterCorpus.Materialize([.. payloads, web, written.Update]));
        Assert.Equal(result, WriterCorpus.Materialize([.. payloads, written.Update, web]));
        Assert.Equal(Convert.FromBase64String(entry.Json.GetProperty("update").GetString()!), written.Update);
    }

    [Fact]
    public void The_corpus_covers_seeds_writes_and_concurrent_edits_of_every_kind_of_content()
    {
        var names = WriterCorpus.Cases.Select(entry => entry.Name).ToArray();
        foreach (var required in new[]
                 {
                     "seed-components-0", "seed-requirement-headings-0", "seed-mark-order-0", "seed-empty", "seed-emoji-surrogates", "seed-overlapping-links",
                     "seed-secret", "seed-overview", "seed-math-table-tasks", "write-math-table-tasks-change-attributes", "write-corpus-node-chart-type-in-first-text",
                     "write-corpus-node-table-type-in-first-text", "write-corpus-node-taskList-swap-first-blocks", "write-corpus-node-mathBlock-append-paragraph",
                     "write-corpus-node-secretReference-bold-in-first-text", "write-editor-session-bold-in-first-text", "write-gc-concurrent-delete-type-in-first-text",
                     "write-overlapping-links-unmark-all", "write-emoji-surrogates-emoji-edit", "write-requirement-headings-0-change-attributes",
                     "concurrent-requirements-0-type-in-first-text-block0", "write-corpus-perf-1m-type-in-first-text",
                 })
            Assert.Contains(required, names);
        Assert.Contains(names, name => name.StartsWith("concurrent-", StringComparison.Ordinal));
        output.WriteLine($"{WriterCorpus.Cases.Count} cases: {string.Join(", ", WriterCorpus.Cases.GroupBy(entry => entry.Kind).Select(group => $"{group.Count()} {group.Key}"))}");
    }

    /// <summary>What a seed and a live write of the largest document (the 1 MB corpus page) take.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Timing_of_the_largest_document()
    {
        var entry = WriterCorpus.Get("write-corpus-perf-1m-type-in-first-text");
        var payloads = WriterCorpus.Payloads(entry);
        var target = entry.Json.GetProperty("target");
        var writer = WriterCorpus.Writer;
        _ = writer.Write(payloads[0], [], target, entry.ClientId);
        _ = writer.Seed(target, 1);
        var seed = new List<double>();
        var write = new List<double>();
        for (var round = 0; round < 5; round++)
        {
            var started = Stopwatch.GetTimestamp();
            _ = writer.Seed(target, 1);
            seed.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            started = Stopwatch.GetTimestamp();
            _ = writer.Write(payloads[0], [], target, entry.ClientId);
            write.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        var median = (List<double> values) => values.Order().ElementAt(values.Count / 2);
        output.WriteLine($"{payloads[0].Length} bytes of state, {Encoding.UTF8.GetByteCount(target.GetRawText())} bytes of JSON: seed {median(seed):0.0} ms, "
            + $"write {median(write):0.0} ms (median of 5)");
        Assert.True(median(write) < 5_000, $"the write took {median(write):0} ms");
    }
}

internal sealed record WriterCase(string Kind, string Name, uint ClientId, JsonElement Json);

internal static class WriterCorpus
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static readonly Lazy<(IReadOnlyList<WriterCase> Cases, Dictionary<string, WriterCase> ByName)> Corpus = new(Load);

    public static IReadOnlyList<WriterCase> Cases => Corpus.Value.Cases;

    public static WriterCase Get(string name) => Corpus.Value.ByName[name];

    public static MarkdownSchemaManifest Schema { get; } = MarkdownSchemaManifest.Parse(File.ReadAllText(Path.Combine(Root, "host-markdown-schema.json")));

    public static YjsDocumentWriter Writer { get; } = new(Schema);

    public static YjsDocumentMaterializer Materializer { get; } = new(Schema);

    public static string Materialize(byte[][] payloads) => Materializer.Materialize(payloads[0], payloads.Skip(1).Select(payload => (ReadOnlyMemory<byte>)payload));

    /// <summary>A case's base payloads: base64, or a path below Fixtures (a corpus payload, gzipped when .gz).</summary>
    public static byte[][] Payloads(WriterCase entry) => [.. entry.Json.GetProperty("base").EnumerateArray().Select(item =>
    {
        var text = item.GetString()!;
        if (!text.Contains('-', StringComparison.Ordinal)) return Convert.FromBase64String(text);
        var bytes = File.ReadAllBytes(Path.Combine(Root, text));
        if (!text.EndsWith(".gz", StringComparison.Ordinal)) return bytes;
        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        gzip.CopyTo(buffer);
        return buffer.ToArray();
    })];

    /// <summary>JSON.stringify of a fixture's JSON, which System.Text.Json read: the materializer's form.</summary>
    public static string Stringify(JsonElement json)
    {
        var output = new StringBuilder();
        JsJson.Value(output, json);
        return output.ToString();
    }

    private static (IReadOnlyList<WriterCase>, Dictionary<string, WriterCase>) Load()
    {
        using var gzip = new GZipStream(File.OpenRead(Path.Combine(Root, "yjs-writer", "cases.json.gz")), CompressionMode.Decompress);
        using var document = JsonDocument.Parse(gzip, new JsonDocumentOptions { MaxDepth = 256 });
        var cases = document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(entry => new WriterCase(entry.GetProperty("kind").GetString()!, entry.GetProperty("name").GetString()!, entry.GetProperty("clientId").GetUInt32(), entry.Clone()))
            .ToArray();
        return (cases, cases.ToDictionary(entry => entry.Name, StringComparer.Ordinal));
    }
}
