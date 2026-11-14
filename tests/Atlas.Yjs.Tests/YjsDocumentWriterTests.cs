using System.Text.Json;
using Atlas.Markdown.Sync;

namespace Atlas.Yjs.Tests;

/// <summary>The writer's bounds and refusals, beside the parity cases.</summary>
public class YjsDocumentWriterTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement Page = Json("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Ahoj"}]}]}""");

    [Fact]
    public void A_write_without_a_client_takes_a_new_one_and_its_state_reads_as_the_target()
    {
        var state = WriterCorpus.Writer.Seed(Page, 7);
        var target = Json("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Ahoj světe"}]}]}""");
        var written = WriterCorpus.Writer.Write(state, [], target);
        var update = YjsUpdateDecoder.Decode(written.Update, long.MaxValue);
        Assert.Single(update.Clients);
        Assert.NotEqual(7UL, update.Clients[0].Client);
        Assert.Equal(WriterCorpus.Stringify(target).Replace("\"type\":\"paragraph\",", "\"type\":\"paragraph\",\"attrs\":{\"textAlign\":null,\"dependencies\":false},", StringComparison.Ordinal),
            WriterCorpus.Materializer.Materialize(written.State));
    }

    [Fact]
    public void Writing_the_document_it_holds_writes_no_struct()
    {
        var state = WriterCorpus.Writer.Seed(Page, 7);
        var written = WriterCorpus.Writer.Write(state, [], Page, 8);
        Assert.Empty(YjsUpdateDecoder.Decode(written.Update, long.MaxValue).Structs);
    }

    [Theory]
    [InlineData("""{"type":"doc","content":[{"type":"script"}]}""", ContentRejectionReason.UnknownNode)]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"a","marks":[{"type":"blink"}]}]}]}""", ContentRejectionReason.UnknownMark)]
    [InlineData("""{"type":"paragraph"}""", ContentRejectionReason.InvalidStructure)]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":""}]}]}""", ContentRejectionReason.InvalidStructure)]
    public void A_target_the_schema_cannot_read_is_refused(string json, ContentRejectionReason reason)
    {
        Assert.Equal(reason, Assert.Throws<YjsWriteException>(() => WriterCorpus.Writer.Seed(Json(json), 1)).Reason);
        Assert.Equal(reason, Assert.Throws<YjsWriteException>(() => WriterCorpus.Writer.Write(WriterCorpus.Writer.Seed(Page, 1), [], Json(json), 2)).Reason);
    }

    [Fact]
    public void A_write_beyond_the_payload_bound_is_refused()
    {
        var writer = new YjsDocumentWriter(WriterCorpus.Schema, maxPayloadBytes: 4096);
        var state = writer.Seed(Page, 1);
        var large = Json($$"""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"{{new string('x', 5000)}}"}]}]}""");
        Assert.Equal(ContentRejectionReason.LimitExceeded, Assert.Throws<YjsWriteException>(() => writer.Write(state, [], large, 2)).Reason);
        Assert.Equal(ContentRejectionReason.LimitExceeded, Assert.Throws<YjsWriteException>(() => writer.Seed(large, 2)).Reason);
    }
}
