using System.Text.Json;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>
/// <see cref="YjsUpdateMerger"/> against Y.mergeUpdates, on the corpus packages/markdown/src/collab/yjsMergeCorpus.test.ts records
/// (Fixtures/yjs-merge): the same document for every payload set, and the same bytes, sliced structs included, unless two payloads
/// hold different structs at one clock.
/// </summary>
public class YjsUpdateMergerTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly Lazy<IReadOnlyList<(string Name, string[] Payloads, string Expected)>> Entries = new(() =>
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Fixtures, "yjs-merge", "manifest.json")));
        return manifest.RootElement.GetProperty("entries").EnumerateArray()
            .Select(entry => (entry.GetProperty("name").GetString()!,
                entry.GetProperty("payloads").EnumerateArray().Select(path => path.GetString()!).ToArray(),
                entry.GetProperty("expected").GetString()!))
            .ToArray();
    });

    public static TheoryData<string> Cases => new(Entries.Value.Select(entry => entry.Name));

    private static byte[] Read(string relative)
        => relative.StartsWith("yjs-materializer/", StringComparison.Ordinal)
            ? MaterializerCorpus.Read(relative["yjs-materializer/".Length..])
            : File.ReadAllBytes(Path.Combine(Fixtures, relative));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Merging_writes_the_bytes_Yjs_writes(string name)
    {
        var entry = Entries.Value.Single(candidate => candidate.Name == name);
        var payloads = entry.Payloads.Select(path => (ReadOnlyMemory<byte>)Read(path)).ToArray();
        var expected = Read(entry.Expected);
        var actual = YjsUpdateMerger.Merge(payloads, MaterializerCorpus.MaxBytes);
        if (expected.AsSpan().SequenceEqual(actual)) return;
        // Where a GC and an item of two payloads start at one clock, Yjs's comparator answers -1 both ways and V8's sort picks by
        // position; either struct is the same deleted clocks. Then only the struct choice may differ, nothing the document holds.
        Assert.Contains(YjsUpdateDecoder.Decode(expected, MaterializerCorpus.MaxBytes).Structs, item => item.Content == YContentKind.Gc);
        static string Shape(byte[] payload)
        {
            var update = YjsUpdateDecoder.Decode(payload, MaterializerCorpus.MaxBytes);
            return string.Join(";", update.Clients.Select(range => $"{range.Client}:{range.Start}-{range.End}"))
                   + "|" + string.Join(";", update.DeleteSet.Select(range => $"{range.Client}:{range.Clock}+{range.Length}"));
        }
        Assert.Equal(Shape(expected), Shape(actual));
        Assert.Equal(YjsDocumentMaterializer.Builtin.Materialize(expected), YjsDocumentMaterializer.Builtin.Materialize(actual));
    }

    /// <summary>The guarantee (YjsUpdateMerger's comment): always the document Y.mergeUpdates' result applies as; its bytes whenever no
    /// clock is in two payloads. Where payloads overlap, Yjs's comparator is asymmetric (a GC and an item at one clock, utils/updates.js
    /// sort) and the struct written may differ.</summary>
    [Fact]
    public void Merging_is_an_equivalent_document_and_byte_identical_when_no_clock_is_in_two_payloads()
    {
        var (disjoint, overlapping) = (0, 0);
        foreach (var entry in Entries.Value)
        {
            var payloads = entry.Payloads.Select(Read).ToArray();
            var expected = Read(entry.Expected);
            var actual = YjsUpdateMerger.Merge([.. payloads.Select(payload => (ReadOnlyMemory<byte>)payload)], MaterializerCorpus.MaxBytes);
            Assert.Equal(YjsDocumentMaterializer.Builtin.Materialize(expected), YjsDocumentMaterializer.Builtin.Materialize(actual));
            var ranges = payloads.SelectMany(payload => YjsUpdateDecoder.Decode(payload, MaterializerCorpus.MaxBytes).Clients).GroupBy(range => range.Client);
            if (ranges.Any(client => client.SelectMany((a, i) => client.Skip(i + 1), (a, b) => a.Start < b.End && b.Start < a.End).Any(overlap => overlap)))
            {
                overlapping++;
                continue;
            }
            Assert.True(expected.AsSpan().SequenceEqual(actual), entry.Name);
            disjoint++;
        }
        Assert.True(disjoint > 0 && overlapping > 0, $"{disjoint} disjoint, {overlapping} overlapping");
    }

    [Fact]
    public void The_corpus_slices_structs_that_another_payload_covers_in_part()
    {
        Assert.True(Entries.Value.Count > 100);
        // A late state merged after an early one: their merged items start at the same clocks and the early one wins.
        Assert.Contains(Entries.Value, entry => entry.Name == "editor-session-states");
        var merged = YjsUpdateMerger.Merge([Read("yjs-merge/editor-session/early.state.bin"), Read("yjs-merge/editor-session/late.state.bin")],
            MaterializerCorpus.MaxBytes);
        Assert.Equal(YjsDocumentMaterializer.Builtin.Materialize(Read("yjs-merge/editor-session/late.state.bin")),
            YjsDocumentMaterializer.Builtin.Materialize(merged));
    }

    [Fact]
    public void A_clock_gap_is_refused_instead_of_written_as_a_skip()
    {
        // One editor's updates without the one between them.
        var steps = MaterializerCorpus.Cases.Single(entry => entry.Name == "editor-session").Steps
            .Select(step => (ReadOnlyMemory<byte>)MaterializerCorpus.Read(step.Payload)).ToArray();
        Assert.Equal(ContentRejectionReason.ClockGap,
            Assert.Throws<ContentRejectedException>(() => YjsUpdateMerger.Merge([steps[1], steps[3]], MaterializerCorpus.MaxBytes)).Reason);
    }
}
