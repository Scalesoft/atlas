using System.Security.Cryptography;
using System.Text.Json;
using Atlas.Markdown.Sync;

namespace Atlas.Markdown.Sync.Tests;

public class SnapshotValidationTests
{
    private static DocSnapshot Snapshot(string json = "{\"type\":\"doc\",\"content\":[]}",
        byte[]? state = null, long sequence = 0, string markdown = "", string plainText = "")
    {
        state ??= [0, 1, 255];
        using var parsed = JsonDocument.Parse(json);
        return new DocSnapshot(state, sequence, new ProjectionSetDto(sequence,
            Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant(), markdown,
            parsed.RootElement.Clone(), plainText));
    }

    public static IEnumerable<object[]> InvalidEnvelopes()
    {
        var valid = Snapshot();
        yield return ["null snapshot", null!];
        yield return ["null state", valid with { YjsState = null! }];
        yield return ["null projections", valid with { Projections = null! }];
        yield return ["negative covered and source", valid with
        {
            CoveredSequence = -1, Projections = valid.Projections with { SourceSequence = -1 },
        }];
        yield return ["minimum covered sequence", valid with { CoveredSequence = long.MinValue }];
        yield return ["negative source", valid with { Projections = valid.Projections with { SourceSequence = -1 } }];
        yield return ["source ahead", valid with { Projections = valid.Projections with { SourceSequence = 1 } }];
        yield return ["source behind", valid with { CoveredSequence = 1 }];
        yield return ["null markdown", valid with { Projections = valid.Projections with { Markdown = null! } }];
        yield return ["null plain text", valid with { Projections = valid.Projections with { PlainText = null! } }];
        yield return ["undefined JSON", valid with { Projections = valid.Projections with { EditorJson = default } }];
        foreach (var hash in new string?[]
        {
            null, "", " ", new('0', 64), new('g', 64), valid.Projections.StateSha256.ToUpperInvariant(),
            valid.Projections.StateSha256[..63], valid.Projections.StateSha256 + "0",
            " " + valid.Projections.StateSha256, valid.Projections.StateSha256 + "\n",
            "sha256:" + valid.Projections.StateSha256,
        })
            yield return [$"invalid hash {hash ?? "null"}", valid with
            {
                Projections = valid.Projections with { StateSha256 = hash! },
            }];
        yield return ["hash of different bytes", valid with { YjsState = [1, 2, 3] }];
    }

    [Theory]
    [MemberData(nameof(InvalidEnvelopes))]
    public void Invalid_envelope_throws_only_invalid_data(string description, DocSnapshot snapshot)
    {
        Assert.False(string.IsNullOrEmpty(description));
        Assert.Throws<InvalidDataException>(() => SnapshotValidation.Prepare(snapshot));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("\"doc\"")]
    [InlineData("{}")]
    [InlineData("{\"type\":null}")]
    [InlineData("{\"type\":0}")]
    [InlineData("{\"type\":[]}")]
    [InlineData("{\"type\":{}}")]
    [InlineData("{\"type\":\"paragraph\"}")]
    [InlineData("{\"type\":\"Doc\"}")]
    [InlineData("""{"type":"doc "}""")]
    [InlineData("{\"type\":\"doc\",\"content\":null}")]
    [InlineData("{\"type\":\"doc\",\"content\":{}}")]
    [InlineData("{\"type\":\"doc\",\"content\":\"\"}")]
    [InlineData("{\"type\":\"doc\",\"content\":0}")]
    [InlineData("{\"type\":\"doc\",\"content\":false}")]
    public void Invalid_editor_root_shape_is_rejected(string json)
        => Assert.Throws<InvalidDataException>(() => SnapshotValidation.Prepare(Snapshot(json)));

    [Theory]
    [InlineData("{\"type\":\"doc\"}", "", "", 0)]
    [InlineData("{\"type\":\"doc\",\"content\":[]}", "", "", 1)]
    [InlineData("{\"type\":\"doc\",\"content\":[]}", "Příliš žluťoučký 🐈 中文", "řádek\n\t日本語", long.MaxValue)]
    [InlineData("{\"type\":\"doc\",\"content\":[{\"type\":\"futurePlugin\",\"attrs\":{\"revision\":5,\"custom\":[true,null,\"Ω\"]}}],\"metadata\":{\"plugin\":\"future\"}}", "", "plugin text", 42)]
    [InlineData("{\"metadata\":{\"arbitrary\":true},\"type\":\"doc\",\"content\":[]}", "**rich**", "rich", 7)]
    public void Valid_envelopes_preserve_text_and_plugin_metadata(string json, string markdown, string text, long sequence)
    {
        var input = Snapshot(json, sequence: sequence, markdown: markdown, plainText: text);
        var prepared = SnapshotValidation.Prepare(input);
        Assert.NotSame(input, prepared);
        Assert.NotSame(input.Projections, prepared.Projections);
        Assert.NotSame(input.YjsState, prepared.YjsState);
        Assert.Equal(input.YjsState, prepared.YjsState);
        Assert.Equal(input.Projections, prepared.Projections with { EditorJson = input.Projections.EditorJson });
        Assert.Equal(json, prepared.Projections.EditorJson.GetRawText());
        Assert.Equal(sequence, prepared.CoveredSequence);
    }

    [Fact]
    public void Empty_state_and_empty_projections_are_valid_envelopes()
    {
        var prepared = SnapshotValidation.Prepare(Snapshot(state: []));
        Assert.Empty(prepared.YjsState);
        Assert.Equal("", prepared.Projections.Markdown);
        Assert.Equal("", prepared.Projections.PlainText);
    }

    [Fact]
    public void Disposed_editor_document_is_rejected_as_invalid_data()
    {
        var input = Snapshot();
        var json = JsonDocument.Parse("{\"type\":\"doc\"}");
        input = input with { Projections = input.Projections with { EditorJson = json.RootElement } };
        json.Dispose();
        Assert.Throws<InvalidDataException>(() => SnapshotValidation.Prepare(input));
    }

    [Fact]
    public void Prepared_snapshot_survives_source_mutation_and_json_disposal()
    {
        byte[] bytes = [10, 20, 30];
        using var json = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[],\"meta\":{\"custom\":\"ř\"}}");
        var input = Snapshot(state: bytes);
        input = input with { Projections = input.Projections with { EditorJson = json.RootElement } };
        var prepared = SnapshotValidation.Prepare(input);
        bytes[0] = 99;
        json.Dispose();

        Assert.Equal(new byte[] { 10, 20, 30 }, prepared.YjsState);
        Assert.Equal("ř", prepared.Projections.EditorJson.GetProperty("meta").GetProperty("custom").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(prepared.YjsState)).ToLowerInvariant(),
            prepared.Projections.StateSha256);
        prepared.YjsState[1] = 55;
        Assert.Equal(20, bytes[1]);
    }

    [Fact]
    public void Separately_prepared_snapshots_do_not_share_mutable_state()
    {
        var input = Snapshot();
        var first = SnapshotValidation.Prepare(input);
        var second = SnapshotValidation.Prepare(input);
        first.YjsState[0] = 99;
        Assert.Equal(0, second.YjsState[0]);
        Assert.Equal(0, input.YjsState[0]);
    }
}
