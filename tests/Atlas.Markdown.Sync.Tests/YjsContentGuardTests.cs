using System.Text.Json;
using static Atlas.Markdown.Sync.Tests.YUpdateBuilder;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>Hand-crafted payloads against a small document: what an attacker who skips the editor can send.</summary>
public class YjsContentGuardTests
{
    // Client 1: paragraph (1:0) > text (1:1) > "hello" (1:2-6); secretReference (1:7) with secretId (1:8). Secret ids are
    // UUIDs because the guard checks every attribute value before the host decides a reserved node.
    private static readonly byte[] Baseline = new YUpdateBuilder().Structs(1, 0,
        Element(At.InRoot(), "paragraph"),
        Text(At.Under(1, 0)),
        Str(At.Under(1, 1), "hello"),
        Element(At.After(1, 0), "secretReference"),
        AnyString(At.Under(1, 7, "secretId"), "00000000-0000-4000-8000-000000000001")).Build();

    private static ContentAdmission Admit(byte[] update, ManagedDocumentContentGuard? guard = null)
        => (guard ?? YjsCorpus.Guard()).Admit(YjsCorpus.Index(Baseline), update);

    private static byte[] Update(params byte[][] structs) => new YUpdateBuilder().Structs(2, 0, structs).Build();

    public static TheoryData<string, byte[], ContentRejectionReason> Hostile => new()
    {
        { "secret attribute rewritten through its previous value", Update(AnyString(At.After(1, 8), "00000000-0000-4000-8000-000000000002")), ContentRejectionReason.ReservedAttribute },
        { "new attribute on an existing secret", Update(AnyString(At.Under(1, 7, "field"), "password")), ContentRejectionReason.ReservedAttribute },
        { "content inside an existing secret", Update(Text(At.Under(1, 7))), ContentRejectionReason.ReservedNode },
        { "secret inserted into the paragraph", Update(Element(At.Under(1, 0), "secretInlineReference"),
            AnyString(At.Under(2, 0, "secretId"), "00000000-0000-4000-8000-000000000002")), ContentRejectionReason.ReservedNode },
        { "unknown node inserted and deleted again",
            new YUpdateBuilder().Structs(2, 0, Element(At.After(1, 0), "evilNode")).Delete(2, 0, 1).Build(), ContentRejectionReason.UnknownNode },
        { "secret inserted and deleted again",
            new YUpdateBuilder().Structs(2, 0, Element(At.After(1, 0), "secretReference")).Delete(2, 0, 1).Build(), ContentRejectionReason.ReservedNode },
        { "unknown attribute", Update(AnyString(At.Under(1, 0, "hiddenPayload"), "x")), ContentRejectionReason.UnknownAttribute },
        { "unknown mark", Update(Format(At.After(1, 6), "evilMark", "{}")), ContentRejectionReason.UnknownMark },
        { "mark named like a secret", Update(Format(At.After(1, 6), "secretReference", "{}")), ContentRejectionReason.UnknownMark },
        { "extra root", Update(Element(At.InRoot("shadow"), "paragraph")), ContentRejectionReason.OtherRoot },
        { "extra root map entry", Update(AnyString(new At(Root: "hidden", Key: "value"), "secret")), ContentRejectionReason.OtherRoot },
        { "text embed", Update(Embed(At.After(1, 6), "{\"secret\":\"x\"}")), ContentRejectionReason.UnsupportedContent },
        { "subdocument", Update(Doc(At.After(1, 0))), ContentRejectionReason.UnsupportedContent },
        { "binary attribute value", Update(Binary(At.Under(1, 0, "textAlign"))), ContentRejectionReason.UnsupportedContent },
        { "legacy JSON content", Update(Json(At.After(1, 6))), ContentRejectionReason.UnsupportedContent },
        { "nested Y.Map", Update(Type(At.After(1, 0), 1)), ContentRejectionReason.UnsupportedContent },
        { "nested Y.XmlFragment", Update(Type(At.After(1, 0), 4)), ContentRejectionReason.UnsupportedContent },
        { "string directly in an element", Update(Str(At.Under(1, 0), "x")), ContentRejectionReason.InvalidStructure },
        { "Any value in text", Update(AnyString(At.Under(1, 1), "x")), ContentRejectionReason.InvalidStructure },
        { "attribute on text", Update(AnyString(At.Under(1, 1, "k"), "v")), ContentRejectionReason.InvalidStructure },
        { "attribute on the root fragment", Update(AnyString(new At(Root: "default", Key: "k"), "v")), ContentRejectionReason.InvalidStructure },
        { "element nested in text", Update(Element(At.After(1, 6), "paragraph")), ContentRejectionReason.InvalidStructure },
        { "clock gap", new YUpdateBuilder().Structs(1, 10, Str(At.After(1, 6), "x")).Build(), ContentRejectionReason.ClockGap },
        { "skip struct", Update(Skip(1), Str(At.After(1, 6), "x")), ContentRejectionReason.ClockGap },
        { "unknown origin", Update(Str(At.After(9, 0), "x")), ContentRejectionReason.UnknownReference },
        { "unknown parent", Update(Str(At.Under(1, 40), "x")), ContentRejectionReason.UnknownReference },
        { "forward reference within a client", Update(Str(At.After(2, 1), "a"), Str(At.After(1, 6), "b")), ContentRejectionReason.UnknownReference },
        { "reference cycle between clients",
            new YUpdateBuilder().Structs(2, 0, Str(At.After(3, 0), "a")).Structs(3, 0, Str(At.After(2, 0), "b")).Build(), ContentRejectionReason.UnknownReference },
        { "delete beyond the known state", new YUpdateBuilder().Delete(1, 5, 10).Build(), ContentRejectionReason.DeleteOutOfRange },
        { "delete of an unknown client", new YUpdateBuilder().Delete(7, 0, 1).Build(), ContentRejectionReason.DeleteOutOfRange },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Hostile_payload_is_refused_without_touching_the_index(string description, byte[] update, ContentRejectionReason reason)
    {
        var guard = YjsCorpus.Guard();
        var index = YjsCorpus.Index(Baseline);
        var verdict = guard.Admit(index, update);
        Assert.False(verdict.IsAccepted, description);
        Assert.Equal(reason, verdict.Reason);
        Assert.True(index.IsEquivalentTo(YjsCorpus.Index(Baseline)), "a rejection must not change the index");
    }

    public static TheoryData<string, byte[]> Honest => new()
    {
        { "typing", Update(Str(At.After(1, 6), " world")) },
        { "new paragraph with text", Update(Element(At.After(1, 0), "paragraph"), Text(At.Under(2, 0)), Str(At.Under(2, 1), "x")) },
        { "known attribute", Update(AnyString(At.Under(1, 0, "textAlign"), "center")) },
        { "new callout with a variant", Update(Element(At.After(1, 0), "callout"), AnyString(At.Under(2, 0, "variant"), "caution"), Element(At.Under(2, 0), "paragraph")) },
        { "mark", Update(Format(At.After(1, 2), "bold", "{}"), Format(At.After(1, 4), "bold", "null")) },
        // y-prosemirror names an overlapping mark "name--" and an eight-character hash.
        { "overlapping mark", Update(Format(At.After(1, 2), "link--AbCd12==", "{\"href\":\"https://x.y\"}")) },
        { "delete the secret", new YUpdateBuilder().Delete(1, 7, 2).Build() },
        { "delete own insert", new YUpdateBuilder().Structs(2, 0, Str(At.After(1, 6), "typo")).Delete(2, 0, 4).Build() },
        { "empty update", [0, 0] },
    };

    [Theory]
    [MemberData(nameof(Honest))]
    public void Editor_shaped_payload_is_admitted(string description, byte[] update)
    {
        var verdict = Admit(update);
        Assert.True(verdict.IsAccepted, $"{description}: {verdict.Reason} {verdict.Detail}");
    }

    [Fact]
    public void Resending_an_admitted_update_is_idempotent()
    {
        var guard = YjsCorpus.Guard();
        var index = YjsCorpus.Index(Baseline);
        var update = Update(Str(At.After(1, 6), " again"));
        Assert.True(guard.Admit(index, update).IsAccepted);
        Assert.True(guard.Admit(index, update).IsAccepted);
        Assert.Equal(6u, index.GetClock(2));
    }

    [Fact]
    public async Task Host_decides_new_secrets_and_rewrites_with_author_peers_and_attributes()
    {
        var host = new RecordingReservedNodeGuard(admit: true);
        var guard = YjsCorpus.Guard(host);
        var context = YjsCorpus.Context("alice", "bob");
        var index = YjsCorpus.Index(Baseline);
        var insert = Update(Element(At.Under(1, 0), "secretInlineReference"), AnyString(At.Under(2, 0, "secretId"), "00000000-0000-4000-8000-000000000003"),
            AnyString(At.Under(2, 0, "field"), "password"));
        Assert.True((await guard.AdmitUpdateAsync(index, context, insert, CancellationToken.None)).IsAccepted);
        var request = Assert.Single(host.Requests);
        Assert.Equal(("doc", "alice"), (request.DocumentId, request.AuthorUserId));
        Assert.Equal(["alice", "bob"], request.PeerUserIds);
        var change = Assert.Single(request.Changes);
        Assert.Equal(("secretInlineReference", true), (change.NodeName, change.IsNewNode));
        Assert.Equal("00000000-0000-4000-8000-000000000003", change.Attributes["secretId"].GetString());
        Assert.Equal("password", change.Attributes["field"].GetString());

        var rewrite = new YUpdateBuilder().Structs(3, 0, AnyString(At.After(1, 8), "00000000-0000-4000-8000-000000000004")).Build();
        Assert.True((await guard.AdmitUpdateAsync(index, context, rewrite, CancellationToken.None)).IsAccepted);
        var rewritten = Assert.Single(host.Requests[1].Changes);
        Assert.Equal(("secretReference", false, "00000000-0000-4000-8000-000000000004"), (rewritten.NodeName, rewritten.IsNewNode, rewritten.Attributes["secretId"].GetString()));
    }

    [Fact]
    public async Task A_rewrite_tells_the_host_the_current_secret_id_and_the_new_one()
    {
        var host = new RecordingReservedNodeGuard(admit: true);
        var guard = YjsCorpus.Guard(host);
        var context = YjsCorpus.Context("alice", "bob");
        var index = YjsCorpus.Index(Baseline);
        var rewrite = new YUpdateBuilder().Structs(3, 0, AnyString(At.After(1, 8), "00000000-0000-4000-8000-000000000004")).Build();
        Assert.True((await guard.AdmitUpdateAsync(index, context, rewrite, CancellationToken.None)).IsAccepted);
        var request = Assert.Single(host.Requests);
        Assert.Equal(ReservedNodePayload.Update, request.Payload);
        var change = Assert.Single(request.Changes);
        Assert.Equal(("00000000-0000-4000-8000-000000000001", "00000000-0000-4000-8000-000000000004"), (change.CurrentSecretId, change.Attributes["secretId"].GetString()));
        Assert.Equal(["secretId"], change.CurrentAttributes.Keys);

        // The admitted rewrite becomes the current value; a new attribute keeps the others.
        var second = new YUpdateBuilder().Structs(3, 1, AnyString(At.After(3, 0), "00000000-0000-4000-8000-000000000005"), AnyString(At.Under(1, 7, "field"), "url")).Build();
        Assert.True((await guard.AdmitUpdateAsync(index, context, second, CancellationToken.None)).IsAccepted);
        var next = Assert.Single(host.Requests[1].Changes);
        Assert.Equal(("00000000-0000-4000-8000-000000000004", "00000000-0000-4000-8000-000000000005", "url"), (next.CurrentSecretId, next.Attributes["secretId"].GetString(), next.Attributes["field"].GetString()));
        Assert.False(next.CurrentAttributes.ContainsKey("field"));

        // A new node has no current value, even when it names the same secret as an existing one.
        var insert = Update(Element(At.Under(1, 0), "secretInlineReference"), AnyString(At.Under(2, 0, "secretId"), "00000000-0000-4000-8000-000000000001"));
        Assert.True((await guard.AdmitUpdateAsync(index, context, insert, CancellationToken.None)).IsAccepted);
        var created = Assert.Single(host.Requests[2].Changes);
        Assert.True(created.IsNewNode);
        Assert.Empty(created.CurrentAttributes);
        Assert.Null(created.CurrentSecretId);
    }

    [Fact]
    public void An_index_rebuilt_from_the_stored_baseline_and_log_still_knows_the_current_secret_id()
    {
        var host = new RecordingReservedNodeGuard(admit: true);
        var guard = YjsCorpus.Guard(host);
        var admitted = new YUpdateBuilder().Structs(3, 0, AnyString(At.After(1, 8), "00000000-0000-4000-8000-000000000004")).Build();
        var context = new DocumentContentContext("doc", "alice", ["alice"], null, Baseline, [new LogUpdate(1, Guid.NewGuid(), "alice", DateTimeOffset.UnixEpoch, admitted)], new object());
        Assert.True(guard.AdmitFromBaseline(context, new YUpdateBuilder().Structs(3, 1, AnyString(At.After(3, 0), "00000000-0000-4000-8000-000000000009")).Build()).IsAccepted);
        Assert.Equal("00000000-0000-4000-8000-000000000004", Assert.Single(Assert.Single(host.Requests).Changes).CurrentSecretId);
    }

    [Fact]
    public void Snapshots_and_seeds_are_told_apart_and_a_snapshot_names_what_was_admitted()
    {
        var host = new RecordingReservedNodeGuard(admit: true);
        var guard = YjsCorpus.Guard(host);
        var (seeded, index) = guard.Seed(Baseline);
        Assert.True(seeded.IsAccepted);
        var seed = Assert.Single(host.Requests);
        Assert.Equal(ReservedNodePayload.Seed, seed.Payload);
        Assert.All(seed.Changes, change => Assert.Empty(change.CurrentAttributes));

        // The same state pushed again: the node is sent as new, but the host learns it was admitted with s-1.
        Assert.True(guard.Snapshot(index!, Baseline).IsAccepted);
        var snapshot = host.Requests[1];
        Assert.Equal(ReservedNodePayload.Snapshot, snapshot.Payload);
        var resent = Assert.Single(snapshot.Changes);
        Assert.True(resent.IsNewNode);
        Assert.Equal(("00000000-0000-4000-8000-000000000001", "00000000-0000-4000-8000-000000000001"), (resent.CurrentSecretId, resent.Attributes["secretId"].GetString()));
    }

    [Fact]
    public void A_request_built_by_a_host_defaults_to_an_update_without_current_attributes()
    {
        var change = new ReservedNodeChange("secretReference", true, new Dictionary<string, JsonElement>());
        var request = new ReservedNodeRequest("doc", "alice", ["alice"], [change]);
        Assert.Equal(ReservedNodePayload.Update, request.Payload);
        Assert.Empty(change.CurrentAttributes);
        Assert.Null(change.CurrentSecretId);
    }

    [Fact]
    public void Host_refusal_rejects_the_payload_and_keeps_the_index()
    {
        var guard = YjsCorpus.Guard(new RecordingReservedNodeGuard(admit: false));
        var index = YjsCorpus.Index(Baseline);
        var insert = Update(Element(At.After(1, 7), "secretReference"), AnyString(At.Under(2, 0, "secretId"), "00000000-0000-4000-8000-000000000003"));
        Assert.Equal(ContentRejectionReason.ReservedNode, guard.Admit(index, insert).Reason);
        Assert.Equal(ContentRejectionReason.ReservedAttribute, guard.Admit(index, Update(AnyString(At.After(1, 8), "00000000-0000-4000-8000-000000000009"))).Reason);
        Assert.Equal(0u, index.GetClock(2));
        // Children of a reserved node are never delegated.
        Assert.Equal(ContentRejectionReason.ReservedNode, YjsCorpus.Guard(new RecordingReservedNodeGuard(admit: true))
            .Admit(index, Update(Text(At.Under(1, 7)))).Reason);
    }

    [Fact]
    public void A_seed_with_secrets_is_delegated_to_the_host_as_new_nodes()
    {
        var state = YjsCorpus.Read("node-secretReference/004-final.state.bin");
        Assert.Equal(ContentRejectionReason.ReservedNode, YjsCorpus.Guard().Seed(state).Admission.Reason);
        var host = new RecordingReservedNodeGuard(admit: true);
        Assert.True(YjsCorpus.Guard(host).Seed(state).Admission.IsAccepted);
        Assert.All(host.Requests.Single().Changes, change => Assert.True(change.IsNewNode));
    }

    public static TheoryData<string, byte[], ContentRejectionReason> Malformed
    {
        get
        {
            byte[] longName = Update(Element(At.After(1, 0), new string('a', 300)));
            var nested = new List<byte>();
            for (var depth = 0; depth < 40; depth++) { nested.Add(117); nested.Add(1); }
            nested.Add(126);
            var deep = Update(Item(8, At.Under(1, 0, "textAlign"), body => { VarUint(body, 1); body.AddRange(nested); }));
            return new()
            {
                { "empty", [], ContentRejectionReason.Malformed },
                { "no delete set", [0], ContentRejectionReason.Malformed },
                { "trailing bytes", [0, 0, 0], ContentRejectionReason.Malformed },
                { "invalid UTF-8", Update(Item(4, At.After(1, 6), body => body.AddRange(new byte[] { 2, 0xC3, 0x28 }))), ContentRejectionReason.Malformed },
                { "overlong UTF-8", Update(Item(4, At.After(1, 6), body => body.AddRange(new byte[] { 2, 0xC0, 0x80 }))), ContentRejectionReason.Malformed },
                { "encoded surrogate", Update(Item(4, At.After(1, 6), body => body.AddRange(new byte[] { 3, 0xED, 0xA0, 0x80 }))), ContentRejectionReason.Malformed },
                { "invalid UTF-8 node name", Update(Item(7, At.After(1, 0), body => body.AddRange(new byte[] { 3, 2, 0xFF, 0xFE }))), ContentRejectionReason.Malformed },
                { "empty string", Update(Str(At.After(1, 6), "")), ContentRejectionReason.Malformed },
                { "string longer than the payload", Update(Item(4, At.After(1, 6), body => body.AddRange(new byte[] { 0xFF, 0xFF, 0x03, 0x61 }))), ContentRejectionReason.Malformed },
                { "integer beyond 2^53", [1, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 0, 0], ContentRejectionReason.Malformed },
                { "overlong varint", [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00, 0], ContentRejectionReason.Malformed },
                { "clock beyond 32 bits", new YUpdateBuilder().Structs(2, 0xFFFFFFFF, Str(At.After(1, 6), "xy")).Build(), ContentRejectionReason.Malformed },
                { "client listed twice", new YUpdateBuilder().Structs(2, 0, Str(At.After(1, 6), "a")).Structs(2, 1, Str(At.After(2, 0), "b")).Build(), ContentRejectionReason.Malformed },
                { "move content ref", Update(Item(11, At.After(1, 6), _ => { })), ContentRejectionReason.Malformed },
                { "unknown type ref", Update(Type(At.After(1, 0), 7)), ContentRejectionReason.Malformed },
                { "unknown Any tag", Update(Item(8, At.Under(1, 0, "textAlign"), body => { VarUint(body, 1); body.Add(100); })), ContentRejectionReason.Malformed },
                { "invalid JSON format value", Update(Format(At.After(1, 2), "bold", "{notjson")), ContentRejectionReason.Malformed },
                { "zero-length delete", new YUpdateBuilder().Delete(1, 0, 0).Build(), ContentRejectionReason.Malformed },
                { "name too long", longName, ContentRejectionReason.LimitExceeded },
                { "value nested too deep", deep, ContentRejectionReason.LimitExceeded },
            };
        }
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_payload_yields_the_typed_rejection(string description, byte[] update, ContentRejectionReason reason)
    {
        var verdict = Admit(update);
        Assert.False(verdict.IsAccepted, description);
        Assert.Equal(reason, verdict.Reason);
    }

    [Fact]
    public void Every_truncation_of_a_real_update_is_refused()
    {
        var update = YjsCorpus.Read("editing/035-paste_rich_html.update.bin");
        var baseline = YjsCorpus.Read("editing/000-baseline.state.bin");
        var guard = YjsCorpus.Guard();
        for (var length = 0; length < update.Length; length++)
            Assert.Equal(ContentRejectionReason.Malformed, guard.Admit(YjsCorpus.Index(baseline), update[..length]).Reason);
    }

    [Fact]
    public void Payloads_above_the_document_size_limit_are_refused_before_decoding()
    {
        var guard = YjsCorpus.Guard(maxDocumentBytes: 16);
        var update = Update(Str(At.After(1, 6), new string('x', 32)));
        Assert.Equal(ContentRejectionReason.LimitExceeded, guard.Admit(YjsCorpus.Index(Baseline), update).Reason);
        Assert.Equal(ContentRejectionReason.LimitExceeded, guard.Seed(Baseline).Admission.Reason);
    }

    [Fact]
    public void Lib0_values_reach_the_host_as_json()
    {
        var any = new List<byte> { 118, 3 };
        String(any, "n"); any.Add(125); any.Add(0x41); // -1
        String(any, "list"); any.Add(117); any.Add(2); any.Add(120); any.Add(126);
        String(any, "f"); any.Add(123); any.AddRange(BitConverter.GetBytes(1.5).Reverse());
        var json = Lib0AnyJson.ToJson(any.ToArray());
        Assert.Equal("""{"n":-1,"list":[true,null],"f":1.5}""", JsonSerializer.Serialize(json));
    }

    private sealed class RecordingReservedNodeGuard(bool admit) : IReservedNodeGuard
    {
        public List<ReservedNodeRequest> Requests { get; } = [];

        public ValueTask<bool> AdmitAsync(ReservedNodeRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return ValueTask.FromResult(admit);
        }
    }
}
