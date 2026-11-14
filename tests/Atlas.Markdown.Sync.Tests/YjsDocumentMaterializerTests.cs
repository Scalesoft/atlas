using System.Text.RegularExpressions;
using static Atlas.Markdown.Sync.Tests.YUpdateBuilder;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>What <see cref="YjsMaterializerCorpusTests"/> cannot record from the editor: refusal reasons, other encoders, other hosts.</summary>
public class YjsDocumentMaterializerTests
{
    private const string EmptyDocument = """{"type":"doc","content":[{"type":"paragraph","attrs":{"textAlign":null}}]}""";

    [Theory]
    [InlineData("unsupported-unknown-node", ContentRejectionReason.UnknownNode)]
    [InlineData("unsupported-unknown-mark", ContentRejectionReason.UnknownMark)]
    [InlineData("unsupported-mark-not-allowed", ContentRejectionReason.InvalidDocument)]
    [InlineData("unsupported-invalid-content", ContentRejectionReason.InvalidDocument)]
    [InlineData("unsupported-text-in-list", ContentRejectionReason.InvalidDocument)]
    [InlineData("unsupported-top-level-list-item", ContentRejectionReason.InvalidDocument)]
    [InlineData("unsupported-text-at-root", ContentRejectionReason.InvalidStructure)]
    [InlineData("unsupported-hook", ContentRejectionReason.UnsupportedContent)]
    [InlineData("unsupported-map-child", ContentRejectionReason.UnsupportedContent)]
    [InlineData("unsupported-any-child", ContentRejectionReason.UnsupportedContent)]
    [InlineData("unsupported-embed", ContentRejectionReason.UnsupportedContent)]
    [InlineData("unsupported-type-attribute", ContentRejectionReason.UnsupportedContent)]
    [InlineData("unsupported-binary-attribute", ContentRejectionReason.UnsupportedContent)]
    [InlineData("unsupported-nested-undefined", ContentRejectionReason.InvalidAttributeValue)]
    [InlineData("unsupported-nan", ContentRejectionReason.InvalidAttributeValue)]
    [InlineData("unsupported-bigint", ContentRejectionReason.InvalidAttributeValue)]
    [InlineData("unsupported-format-string", ContentRejectionReason.InvalidAttributeValue)]
    [InlineData("unsupported-pending", ContentRejectionReason.ClockGap)]
    public void An_unsupported_structure_is_refused_with_its_reason(string name, ContentRejectionReason reason)
    {
        var payloads = MaterializerCorpus.Cases.Single(entry => entry.Name == name).Steps.Select(step => MaterializerCorpus.Read(step.Payload)).ToArray();
        var refusal = Assert.Throws<YjsMaterializationException>(() =>
            YjsDocumentMaterializer.Builtin.Materialize(payloads[0], payloads.Skip(1).Select(payload => (ReadOnlyMemory<byte>)payload)));
        Assert.Equal(reason, refusal.Reason);
    }

    [Fact]
    public void A_payload_that_is_no_Yjs_update_is_refused()
    {
        var refusal = Assert.Throws<YjsMaterializationException>(() => YjsDocumentMaterializer.Builtin.Materialize(new byte[] { 1, 2, 3 }));
        Assert.Equal(ContentRejectionReason.Malformed, refusal.Reason);
    }

    /// <summary>
    /// lib0 writes an object in JavaScript's key order; another encoder (yrs) may not. A JavaScript reader puts array index keys first in
    /// ascending order, keeps the first position and the last value of a repeated key.
    /// </summary>
    [Fact]
    public void Object_keys_come_out_in_JavaScript_order_whoever_encoded_them()
    {
        var state = Paragraph(value =>
        {
            Object(value, ("b", Int(1)), ("10", Int(2)), ("2", Int(3)), ("a", Int(4)), ("b", Int(5)), ("4294967295", Int(6)));
        });
        Assert.Equal("""{"type":"doc","content":[{"type":"paragraph","attrs":{"textAlign":{"2":3,"10":2,"b":5,"a":4,"4294967295":6}}}]}""",
            YjsDocumentMaterializer.Builtin.Materialize(state));
    }

    [Fact]
    public void A_proto_key_which_a_JavaScript_reader_turns_into_a_prototype_is_refused()
    {
        var state = Paragraph(value => Object(value, ("__proto__", Int(1))));
        Assert.Equal(ContentRejectionReason.InvalidAttributeValue,
            Assert.Throws<YjsMaterializationException>(() => YjsDocumentMaterializer.Builtin.Materialize(state)).Reason);
    }

    [Theory]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    [InlineData(-1e21, "-1e+21")]
    [InlineData(1.234e-7, "1.234e-7")]
    [InlineData(123e-20, "1.23e-18")]
    [InlineData(1e20, "100000000000000000000")]
    [InlineData(0.000001, "0.000001")]
    [InlineData(-0.0, "0")]
    [InlineData(25.0, "25")]
    public void Numbers_are_written_as_JavaScript_writes_them(double value, string expected)
        => Assert.Equal(expected, JsJson.Number(value));

    [Fact]
    public void A_host_binding_another_fragment_names_it()
    {
        var state = new YUpdateBuilder().Structs(1, 0, Element(At.InRoot("content"), "paragraph"), Text(At.Under(1, 0)), Str(At.Under(1, 1), "ahoj")).Build();
        Assert.Equal("""{"type":"doc","content":[{"type":"paragraph","attrs":{"textAlign":null},"content":[{"type":"text","text":"ahoj"}]}]}""",
            new YjsDocumentMaterializer(MarkdownSchemaManifest.Builtin, root: "content").Materialize(state));
        Assert.Equal(EmptyDocument, YjsDocumentMaterializer.Builtin.Materialize(state));
    }

    /// <summary>The editor of a host without a plugin has no schema for its nodes (y-tiptap deletes them) and drops its attributes.</summary>
    [Fact]
    public void A_host_schema_without_a_plugin_materializes_what_its_editor_shows()
    {
        var plugins = MarkdownSchemaManifest.Builtin.Plugins.Keys;
        var page = MaterializerCorpus.Read(MaterializerCorpus.Cases.Single(entry => entry.Name == "fixture-imported-server-github-deployment-guide").Steps[0].Payload);
        var withAnchors = YjsDocumentMaterializer.Builtin.Materialize(page);
        var withoutAnchors = new YjsDocumentMaterializer(MarkdownSchemaManifest.Builtin.WithPlugins(plugins.Where(id => id != "heading-anchors"))).Materialize(page);
        Assert.Contains("\"headingId\":", withAnchors);
        Assert.DoesNotContain("headingId", withoutAnchors);
        Assert.Equal(Regex.Replace(withAnchors, "(?:\"headingId\":(?:\"[^\"]*\"|null)|\"aliases\":\"[^\"]*\"),", ""), withoutAnchors);
        var everything = MaterializerCorpus.Read(MaterializerCorpus.Cases.Single(entry => entry.Name == "fixture-web-document").Steps[0].Payload);
        var withoutMath = new YjsDocumentMaterializer(MarkdownSchemaManifest.Builtin.WithPlugins(plugins.Where(id => id != "math")));
        Assert.Equal(ContentRejectionReason.UnknownNode, Assert.Throws<YjsMaterializationException>(() => withoutMath.Materialize(everything)).Reason);
    }

    /// <summary>Keystrokes merge into one run, a delete and an insert split it; a payload the guard rolls back restores the split text.</summary>
    [Fact]
    public void Runs_keep_their_text_when_they_merge_split_and_roll_back()
    {
        var index = new YjsDocumentIndex(tree: true, text: true);
        index.ApplyTrusted(new YUpdateBuilder().Structs(1, 0, Element(At.InRoot(), "paragraph"), Text(At.Under(1, 0)), Str(At.Under(1, 1), "hello")).Build(), MaterializerCorpus.MaxBytes);
        for (uint clock = 0; clock < 100; clock++)
            index.ApplyTrusted(new YUpdateBuilder().Structs(2, clock, Str(new At(Origin: clock == 0 ? (1UL, 6u) : (2UL, clock - 1)), "x")).Build(), MaterializerCorpus.MaxBytes);
        index.ApplyTrusted(new YUpdateBuilder().Delete(2, 10, 20).Build(), MaterializerCorpus.MaxBytes);
        static string Document(string text) => $$"""{"type":"doc","content":[{"type":"paragraph","attrs":{"textAlign":null},"content":[{"type":"text","text":"{{text}}"}]}]}""";
        var typed = Document("hello" + new string('x', 80));
        Assert.Equal(typed, YjsDocumentMaterializer.Builtin.Materialize(index.Tree!));
        var insert = new YUpdateBuilder().Structs(3, 0, Str(new At(Origin: (2, 50), Right: (2, 51)), "Y")).Build();
        var parsed = YjsUpdateDecoder.Decode(insert, MaterializerCorpus.MaxBytes);
        YjsParentResolver.Resolve(parsed, index);
        index.Tree!.Apply(insert, parsed, journal: true);
        index.Tree.Rollback();
        Assert.Equal(typed, YjsDocumentMaterializer.Builtin.Materialize(index.Tree));
        index.ApplyTrusted(insert, MaterializerCorpus.MaxBytes);
        Assert.Equal(Document("hello" + new string('x', 31) + "Y" + new string('x', 49)), YjsDocumentMaterializer.Builtin.Materialize(index.Tree));
    }

    /// <summary>The editor refuses documents deeper than the validator's bound; the materializer refuses them before its recursion does.</summary>
    [Fact]
    public void A_document_nested_deeper_than_the_editor_allows_is_refused()
    {
        var structs = Enumerable.Range(0, 100).Select(level => Element(level == 0 ? At.InRoot() : At.Under(1, (uint)level - 1), "blockquote")).ToArray();
        var refusal = Assert.Throws<YjsMaterializationException>(() => YjsDocumentMaterializer.Builtin.Materialize(new YUpdateBuilder().Structs(1, 0, structs).Build()));
        Assert.Equal(ContentRejectionReason.LimitExceeded, refusal.Reason);
    }

    /// <summary>A paragraph whose textAlign attribute holds one lib0 Any value written by <paramref name="value"/>.</summary>
    private static byte[] Paragraph(Action<List<byte>> value)
        => new YUpdateBuilder().Structs(1, 0, Element(At.InRoot(), "paragraph"), Item(8, At.Under(1, 0, "textAlign"), body =>
        {
            VarUint(body, 1);
            value(body);
        })).Build();

    private static Action<List<byte>> Int(int value) => body =>
    {
        body.Add(125);
        body.Add((byte)value); // lib0 varint of 0..63
    };

    private static void Object(List<byte> body, params (string Key, Action<List<byte>> Value)[] entries)
    {
        body.Add(118);
        VarUint(body, (ulong)entries.Length);
        foreach (var (key, write) in entries)
        {
            String(body, key);
            write(body);
        }
    }
}
