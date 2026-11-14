using static Atlas.Markdown.Sync.Tests.YUpdateBuilder;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>
/// The guard's full validation of collaborative content: every attribute value a payload writes, and the document it produces,
/// by the editor's rules (MarkdownDocumentValidator). Crafted payloads stand for a client that skips the editor; the real
/// editor's traffic (the corpus) is admitted by YjsCorpusTests.
/// </summary>
public sealed class YjsDocumentValidationTests
{
    // Client 1: paragraph (1:0) > text (1:1) > "hello" (1:2-6).
    private static readonly byte[] Baseline = new YUpdateBuilder().Structs(1, 0,
        Element(At.InRoot(), "paragraph"),
        Text(At.Under(1, 0)),
        Str(At.Under(1, 1), "hello")).Build();

    private static byte[] Update(params byte[][] structs) => new YUpdateBuilder().Structs(2, 0, structs).Build();

    /// <summary>Between two characters, as the editor writes a format (an item without a right origin goes to the end of the run).</summary>
    private static At Between((ulong, uint) left, (ulong, uint) right) => new(Origin: left, Right: right);

    private static byte[] Number(At at, double value) => Item(8, at, body => { VarUint(body, 1); body.Add(123); body.AddRange(BitConverter.GetBytes(value).Reverse()); });

    public static TheoryData<string, byte[], ContentRejectionReason> Invalid => new()
    {
        { "unknown alignment", Update(AnyString(At.Under(1, 0, "textAlign"), "justify")), ContentRejectionReason.InvalidAttributeValue },
        { "an invalid value overwritten in the same payload", Update(AnyString(At.Under(1, 0, "textAlign"), "justify"), AnyString(At.After(2, 0), "center")),
            ContentRejectionReason.InvalidAttributeValue },
        { "heading level as text", Update(Element(At.After(1, 0), "heading"), AnyString(At.Under(2, 0, "level"), "2")), ContentRejectionReason.InvalidAttributeValue },
        { "heading level 7", Update(Element(At.After(1, 0), "heading"), Number(At.Under(2, 0, "level"), 7)), ContentRejectionReason.InvalidAttributeValue },
        { "non-finite number", Update(Element(At.After(1, 0), "heading"), Number(At.Under(2, 0, "level"), double.NaN)), ContentRejectionReason.InvalidAttributeValue },
        { "an unsafe diagram", Update(Element(At.After(1, 0), "diagram"), Item(8, At.Under(2, 0, "data"), body =>
            {
                VarUint(body, 1);
                body.Add(118); VarUint(body, 3);
                String(body, "version"); body.Add(125); body.Add(1);
                String(body, "source"); body.Add(119); String(body, "<script>alert(1)</script>");
                String(body, "caption"); body.Add(119); String(body, "");
            })), ContentRejectionReason.InvalidAttributeValue },
        { "a script link", Update(Format(Between((1, 2), (1, 3)), "link", "{\"href\":\"java\\nscript:alert(1)\"}")), ContentRejectionReason.InvalidDocument },
        { "a list item at the top level", Update(Element(At.After(1, 0), "listItem"), Element(At.Under(2, 0), "paragraph")), ContentRejectionReason.InvalidDocument },
        { "an inline node at the top level", Update(Element(At.After(1, 0), "hardBreak")), ContentRejectionReason.InvalidDocument },
        { "a block in a paragraph", Update(Element(At.Under(1, 0), "horizontalRule")), ContentRejectionReason.InvalidDocument },
        { "a page reference without its required fields", Update(Element(At.Under(1, 0), "pageReference")), ContentRejectionReason.InvalidDocument },
        { "bold in a code block", Update(Element(At.After(1, 0), "codeBlock"), Text(At.Under(2, 0)), Format(At.Under(2, 1), "bold", "{}"), Str(At.After(2, 2), "x")),
            ContentRejectionReason.InvalidDocument },
        { "column widths as text", Update(Element(At.After(1, 0), "tableCell"), Item(8, At.Under(2, 0, "colwidth"), body =>
            {
                VarUint(body, 1);
                body.Add(117); VarUint(body, 1); body.Add(119); String(body, "wide");
            })), ContentRejectionReason.InvalidAttributeValue },
        { "a link mark without attributes", Update(Format(Between((1, 2), (1, 3)), "link", "true")), ContentRejectionReason.InvalidDocument },
        { "a list in a table row", Update(Element(At.After(1, 0), "table"), Element(At.Under(2, 0), "bulletList")), ContentRejectionReason.InvalidDocument },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void An_invalid_payload_is_refused_and_leaves_the_document_as_it_was(string description, byte[] update, ContentRejectionReason reason)
    {
        var guard = YjsCorpus.Guard();
        var index = YjsCorpus.Index(Baseline);
        var verdict = guard.Admit(index, update);
        Assert.True(verdict.Reason == reason, $"{description}: {verdict.Reason} {verdict.Detail}");
        Assert.True(index.IsEquivalentTo(YjsCorpus.Index(Baseline)), "a rejection must not change the index");
        // The replica was rolled back: an honest edit that follows is judged on the admitted document.
        Assert.True(guard.Admit(index, Update(Str(At.After(1, 6), "!"))).IsAccepted, description);
        Assert.Equal(["paragraph"], TopLevel(index));
    }

    public static TheoryData<string, byte[]> Valid => new()
    {
        { "a callout with a paragraph", Update(Element(At.After(1, 0), "callout"), AnyString(At.Under(2, 0, "variant"), "tip"), Element(At.Under(2, 0), "paragraph")) },
        { "a heading of level 6", Update(Element(At.After(1, 0), "heading"), Number(At.Under(2, 0, "level"), 6)) },
        { "a link", Update(Format(Between((1, 2), (1, 3)), "link", "{\"href\":\"https://example.com\",\"target\":\"_blank\"}"), Format(Between((1, 4), (1, 5)), "link", "null")) },
        { "an attribute set to undefined (the default)", Update(Item(8, At.Under(1, 0, "textAlign"), body => { VarUint(body, 1); body.Add(127); })) },
        { "a full table", Update(
            Element(At.After(1, 0), "table"),
            Element(At.Under(2, 0), "tableRow"),
            Element(At.Under(2, 1), "tableCell"), Number(At.Under(2, 2, "colspan"), 2), Element(At.Under(2, 2), "paragraph"),
            Element(At.After(2, 1), "tableRow"),
            Element(At.Under(2, 5), "tableCell"), Element(At.Under(2, 6), "paragraph"),
            Element(At.After(2, 6), "tableCell"), Element(At.Under(2, 8), "paragraph")) },
        { "deleting everything (y-prosemirror fills an empty document)", new YUpdateBuilder().Delete(1, 0, 1).Build() },
    };

    [Theory]
    [MemberData(nameof(Valid))]
    public void An_editor_shaped_payload_is_admitted(string description, byte[] update)
    {
        var verdict = YjsCorpus.Guard().Admit(YjsCorpus.Index(Baseline), update);
        Assert.True(verdict.IsAccepted, $"{description}: {verdict.Reason} {verdict.Detail}");
    }

    [Fact]
    public async Task A_document_bound_to_a_schema_may_only_hold_the_content_of_its_plugins()
    {
        var guard = YjsCorpus.Guard();
        var math = Update(Element(At.Under(1, 0), "mathInline"));
        DocumentContentContext Bound(params MarkdownSchemaPlugin[] plugins)
            => new("doc", "alice", ["alice"], new MarkdownDocumentSchema(3, "rich", plugins), null, [], new object());
        var refused = await guard.AdmitUpdateAsync(YjsCorpus.Index(Baseline), Bound(), math, CancellationToken.None);
        Assert.Equal(ContentRejectionReason.InvalidDocument, refused.Reason);
        Assert.Contains("missing_plugin", refused.Detail);
        Assert.True((await guard.AdmitUpdateAsync(YjsCorpus.Index(Baseline), Bound(new MarkdownSchemaPlugin("math", 1)), math, CancellationToken.None)).IsAccepted);
    }

    // A quote with one paragraph (1:0 > 1:1 > 1:2 "q") and a 2×2 table (1:4 > rows 1:5 and 1:10 > cells with a paragraph each).
    private static readonly byte[] Blocks = new YUpdateBuilder().Structs(1, 0,
        Element(At.InRoot(), "blockquote"), Element(At.Under(1, 0), "paragraph"), Text(At.Under(1, 1)), Str(At.Under(1, 2), "q"),
        Element(At.After(1, 0), "table"),
        Element(At.Under(1, 4), "tableRow"), Element(At.Under(1, 5), "tableCell"), Element(At.Under(1, 6), "paragraph"),
        Element(At.After(1, 6), "tableCell"), Element(At.Under(1, 8), "paragraph"),
        Element(At.After(1, 5), "tableRow"), Element(At.Under(1, 10), "tableCell"), Element(At.Under(1, 11), "paragraph"),
        Element(At.After(1, 11), "tableCell"), Element(At.Under(1, 13), "paragraph")).Build();

    /// <summary>
    /// What merging two valid concurrent edits leaves, found by YjsGuardFuzzTests in real editor sessions: the editor shows and
    /// repairs it, so refusing it would stop the session of the peer whose update arrives second (ValidateNode, merged). Moving
    /// such an element or restoring it by undo creates it anew, so new content is judged the same way.
    /// </summary>
    public static TheoryData<string, byte[], byte[]> MergeResults => new()
    {
        // One peer formats text as code while another's bold (or a link) covers it; or a peer types where a format is in force
        // that its own document had dropped (Yjs cleans formats up on receipt, differently per history).
        { "code over bold text", Baseline, Update(Format(Between((1, 2), (1, 3)), "code", "{}"), Format(Between((2, 0), (1, 3)), "bold", "{}")) },
        // Two peers delete the two paragraphs of a quote (or the two items of a list).
        { "an emptied quote", Blocks, new YUpdateBuilder().Delete(1, 1, 3).Build() },
        // One peer adds a column while another adds a row, or deletes a cell's column while another merges it.
        { "a row with a cell missing", Blocks, new YUpdateBuilder().Delete(1, 13, 2).Build() },
        // Two peers delete the two rows (or the last columns) of a table.
        { "a table without rows", Blocks, new YUpdateBuilder().Delete(1, 5, 10).Build() },
        // One peer merges two cells (colspan 2) while another resizes the column (one width).
        { "a moved table that lost a cell", Baseline, Update(
            Element(At.After(1, 0), "table"),
            Element(At.Under(2, 0), "tableRow"),
            Element(At.Under(2, 1), "tableCell"), Number(At.Under(2, 2, "colspan"), 2), Element(At.Under(2, 2), "paragraph"),
            Element(At.After(2, 1), "tableRow"),
            Element(At.Under(2, 5), "tableCell"), Element(At.Under(2, 6), "paragraph")) },
        { "a restored quote whose paragraphs a peer deleted", Baseline, Update(Element(At.After(1, 0), "blockquote")) },
        { "a merged cell with one column width", Blocks, Update(Number(At.Under(1, 6, "colspan"), 2), Item(8, At.Under(1, 6, "colwidth"), body =>
            {
                VarUint(body, 1);
                body.Add(117); VarUint(body, 1); body.Add(123); body.AddRange(BitConverter.GetBytes(120d).Reverse());
            })) },
    };

    [Theory]
    [MemberData(nameof(MergeResults))]
    public void A_concurrent_merge_result_is_admitted(string description, byte[] baseline, byte[] update)
    {
        var verdict = YjsCorpus.Guard().Admit(YjsCorpus.Index(baseline), update);
        Assert.True(verdict.IsAccepted, $"{description}: {verdict.Reason} {verdict.Detail}");
    }

    [Fact]
    public void A_seed_or_snapshot_is_validated_whole()
    {
        var guard = YjsCorpus.Guard();
        Assert.True(guard.Seed(Baseline).Admission.IsAccepted);
        var invalid = new YUpdateBuilder().Structs(1, 0, Element(At.InRoot(), "listItem"), Element(At.Under(1, 0), "paragraph")).Build();
        Assert.Equal(ContentRejectionReason.InvalidDocument, guard.Seed(invalid).Admission.Reason);
        var deep = new YUpdateBuilder().Structs(1, 0, [.. Enumerable.Range(0, 70).Select(level => level == 0
            ? Element(At.InRoot(), "blockquote") : Element(At.Under(1, (uint)level - 1), level == 69 ? "paragraph" : "blockquote"))]).Build();
        var verdict = guard.Seed(deep).Admission;
        Assert.Equal(ContentRejectionReason.InvalidDocument, verdict.Reason);
        Assert.Contains("too_deep", verdict.Detail);
    }

    [Fact]
    public void A_policy_without_a_validator_stays_an_allowlist()
    {
        var policy = new YjsContentPolicy(["default"], new Dictionary<string, IReadOnlyCollection<string>> { ["paragraph"] = ["textAlign"], ["callout"] = [] },
            ["bold"], []);
        var guard = new ManagedDocumentContentGuard(policy, new MarkdownSyncOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ManagedDocumentContentGuard>.Instance);
        var index = new YjsDocumentIndex();
        index.ApplyTrusted(Baseline, 1 << 20);
        Assert.True(guard.Admit(index, Update(Element(At.After(1, 0), "callout"), AnyString(At.Under(1, 0, "textAlign"), "justify"))).IsAccepted);
    }

    private static string[] TopLevel(YjsDocumentIndex index)
    {
        var names = new List<string>();
        for (var item = index.Tree!.Root("default")!.Start; item is not null; item = item.Right)
            if (!item.Deleted && item.Type is { } type) names.Add(type.Name!);
        return [.. names];
    }
}
