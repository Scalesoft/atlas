using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>
/// <see cref="YjsDocumentMaterializer"/> against the editor, on the corpus packages/markdown/src/collab/yjsMaterializerCorpus.test.ts
/// records (Fixtures/yjs-materializer): after every step of every case (a full state, then updates), exactly the JSON.stringify of
/// the document the editor shows, or a refusal where the editor cannot show it or the structure is one it never writes.
/// </summary>
public class YjsMaterializerCorpusTests(ITestOutputHelper output)
{
    public static TheoryData<string> Cases => new(MaterializerCorpus.Cases.Select(entry => entry.Name));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_step_materializes_exactly_the_document_the_editor_shows(string name)
    {
        var entry = MaterializerCorpus.Cases.Single(candidate => candidate.Name == name);
        var payloads = entry.Steps.Select(step => MaterializerCorpus.Read(step.Payload)).ToArray();
        var expected = MaterializerCorpus.Expected(entry);
        // A host that keeps the document applies each step to it; after a refused step it starts again from the payloads.
        YjsDocumentIndex? index = new(tree: true, text: true);
        for (var step = 0; step < entry.Steps.Length; step++)
        {
            var current = entry.Steps[step];
            var history = payloads[..(step + 1)];
            if (current.Expect == "error")
            {
                var refusal = Assert.Throws<YjsMaterializationException>(() => Materialize(history));
                Assert.False(string.IsNullOrEmpty(refusal.Detail));
                index = null;
                continue;
            }
            string? actual = null;
            if (index is not null)
            {
                index.ApplyTrusted(payloads[step], MaterializerCorpus.MaxBytes);
                if (current.Expect != "none") actual = YjsDocumentMaterializer.Builtin.Materialize(index.Tree!);
            }
            else if (current.Expect != "none") actual = Materialize(history);
            if (actual is null) continue;
            Check(current, expected[step], actual, $"{name} step {step}");
            // The public entry point, from the payloads, gives the same document.
            if (step == entry.Steps.Length - 1 && index is not null) Assert.Equal(actual, Materialize(history));
        }
    }

    [Fact]
    public void The_corpus_covers_the_cases_the_conformance_plan_names()
    {
        var names = MaterializerCorpus.Cases.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[]
                 {
                     "fixture-web-document", "fixture-imported-server-github-deployment-guide", "fixture-imported-drop-obsidian-meeting-notes",
                     "fixture-imported-server-wikijs-ops-incident-policy", "fixture-secrets-v4", "editor-unicode", "editor-escapes", "editor-marks",
                     "editor-empty-blocks", "empty-document", "editor-session", "editor-concurrent", "gc-concurrent-delete", "gc-everything-deleted",
                     "raw-surrogate-split", "raw-numbers", "corpus-node-chart", "corpus-node-contactCard", "corpus-node-diagram", "corpus-node-mathBlock",
                     "corpus-node-table", "corpus-node-taskList", "corpus-node-secretReference", "corpus-perf-1m", "near-4m",
                 })
            Assert.Contains(required, names);
        var steps = MaterializerCorpus.Cases.SelectMany(entry => entry.Steps).ToArray();
        output.WriteLine($"{MaterializerCorpus.Cases.Count} cases, {steps.Length} steps: {steps.Count(step => step.Expect == "json")} exact JSON, "
            + $"{steps.Count(step => step.Expect == "sha256")} by hash, {steps.Count(step => step.Expect == "error")} refused, {steps.Count(step => step.Expect == "none")} only applied");
    }

    /// <summary>
    /// What the materialization of the largest document the hub admits (4 MiB, MaxDocumentBytes' default) holds the document's lock
    /// for: decoding and integrating the state, then writing the JSON.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Materializing_a_document_near_the_4_MiB_limit_fits_under_a_document_lock()
    {
        var entry = MaterializerCorpus.Cases.Single(candidate => candidate.Name == "near-4m");
        var state = MaterializerCorpus.Read(entry.Steps[0].Payload);
        Assert.InRange(state.Length, 4_000_000, 4L * 1024 * 1024);
        var materializer = YjsDocumentMaterializer.Builtin;
        _ = materializer.Materialize(state); // JIT and the schema's lazy parts
        var integrate = new List<double>();
        var write = new List<double>();
        var total = new List<double>();
        string? json = null;
        var items = 0;
        for (var round = 0; round < 5; round++)
        {
            var started = Stopwatch.GetTimestamp();
            var index = YjsDocumentIndex.Build(state, [], MaterializerCorpus.MaxBytes, tree: true, text: true);
            var integrated = Stopwatch.GetTimestamp();
            json = materializer.Materialize(index.Tree!);
            var written = Stopwatch.GetTimestamp();
            integrate.Add(Stopwatch.GetElapsedTime(started, integrated).TotalMilliseconds);
            write.Add(Stopwatch.GetElapsedTime(integrated, written).TotalMilliseconds);
            started = Stopwatch.GetTimestamp();
            _ = materializer.Materialize(state);
            total.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            items = index.Tree!.ItemCount;
        }
        Check(entry.Steps[0], "", json!, "near-4m");
        var median = (List<double> values) => values.Order().ElementAt(values.Count / 2);
        output.WriteLine($"{state.Length} bytes, {items} items, {Encoding.UTF8.GetByteCount(json!)} bytes of JSON: decode and integrate {median(integrate):0.0} ms, "
            + $"write {median(write):0.0} ms, Materialize {median(total):0.0} ms (median of 5, min {total.Min():0.0} ms)");
        // A smoke bound for a pathological regression only (about 0.3 s on a developer machine): the runners vary.
        Assert.True(median(total) < 3_000, $"materializing took {median(total):0} ms");
    }

    private static string Materialize(byte[][] payloads)
        => YjsDocumentMaterializer.Builtin.Materialize(payloads[0], payloads.Skip(1).Select(payload => (ReadOnlyMemory<byte>)payload));

    private static void Check(MaterializerStep step, string expected, string actual, string label)
    {
        if (step.Expect == "json")
        {
            Assert.Equal(expected, actual);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(actual);
        Assert.True(step.Bytes == bytes.Length && step.Sha256 == Convert.ToHexStringLower(SHA256.HashData(bytes)),
            $"{label}: {bytes.Length} bytes of JSON with another hash than the editor's {step.Bytes}");
    }
}

/// <summary>Fixtures/yjs-materializer: cases.json and, per case, payloads and expected.jsonl (one line per step).</summary>
internal static class MaterializerCorpus
{
    public const long MaxBytes = 4L * 1024 * 1024;
    private static readonly Lazy<IReadOnlyList<MaterializerCase>> Loaded = new(Load);

    public static string Root => Path.Combine(AppContext.BaseDirectory, "Fixtures", "yjs-materializer");

    public static IReadOnlyList<MaterializerCase> Cases => Loaded.Value;

    public static byte[] Read(string payload)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root, payload));
        if (!payload.EndsWith(".gz", StringComparison.Ordinal)) return bytes;
        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var plain = new MemoryStream();
        gzip.CopyTo(plain);
        return plain.ToArray();
    }

    /// <summary>The expected JSON of each step ("" where the step has none).</summary>
    public static string[] Expected(MaterializerCase entry)
        => File.ReadAllText(Path.Combine(Root, entry.Name, "expected.jsonl"), Encoding.UTF8).Split('\n').Take(entry.Steps.Length)
            .Select(line => line.TrimEnd('\r')).ToArray();

    private static IReadOnlyList<MaterializerCase> Load()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "cases.json")));
        return manifest.RootElement.GetProperty("cases").EnumerateArray()
            .Select(entry => new MaterializerCase(entry.GetProperty("name").GetString()!, entry.GetProperty("steps").EnumerateArray()
                .Select(step => new MaterializerStep(step.GetProperty("payload").GetString()!, step.GetProperty("expect").GetString()!,
                    step.TryGetProperty("sha256", out var hash) ? hash.GetString() : null, step.TryGetProperty("bytes", out var bytes) ? bytes.GetInt32() : 0))
                .ToArray()))
            .ToArray();
    }
}

internal sealed record MaterializerCase(string Name, MaterializerStep[] Steps);

internal sealed record MaterializerStep(string Payload, string Expect, string? Sha256, int Bytes);
