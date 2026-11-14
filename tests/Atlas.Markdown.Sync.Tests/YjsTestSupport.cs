using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>
/// The Yjs boundary corpus (Fixtures/yjs-corpus, see its README): real editor updates and states for every node type,
/// editing scenarios, hostile payloads, perf documents and inputs that crashed native yrs. The manifest verdicts come
/// from a JavaScript observer oracle; set ATLAS_YJS_CORPUS to run against a fuller copy (for example with perf/4m).
/// </summary>
internal static class YjsCorpus
{
    private static readonly Lazy<IReadOnlyList<CorpusEntry>> Entries = new(LoadManifest);

    public static string Root => Environment.GetEnvironmentVariable("ATLAS_YJS_CORPUS") is { Length: > 0 } root
        ? root
        : Path.Combine(AppContext.BaseDirectory, "Fixtures", "yjs-corpus");

    public static IReadOnlyList<CorpusEntry> Manifest => Entries.Value;

    public static byte[] Read(string relative) => File.ReadAllBytes(Path.Combine(Root, relative));

    public static ManagedDocumentContentGuard Guard(IReservedNodeGuard? reservedNodes = null, long maxDocumentBytes = 4L * 1024 * 1024)
        => new(YjsContentPolicy.CreateDefault(), new MarkdownSyncOptions { MaxDocumentBytes = maxDocumentBytes },
            NullLogger<ManagedDocumentContentGuard>.Instance, reservedNodes);

    public static DocumentContentContext Context(string author = "alice", params string[] peers)
        => new("doc", author, [author, .. peers], null, null, [], new object());

    public static ContentAdmission Admit(this ManagedDocumentContentGuard guard, YjsDocumentIndex index, byte[] update)
        => guard.AdmitUpdateAsync(index, Context(), update, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <summary>Standalone snapshot check; the seed index when accepted.</summary>
    public static (ContentAdmission Admission, YjsDocumentIndex? Seeded) Seed(this ManagedDocumentContentGuard guard, byte[] state)
        => guard.AdmitSnapshotAsync(null, Context(), state, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public static ContentAdmission Snapshot(this ManagedDocumentContentGuard guard, YjsDocumentIndex admitted, byte[] state)
        => guard.AdmitSnapshotAsync(admitted, Context(), state, CancellationToken.None).AsTask().GetAwaiter().GetResult().Admission;

    /// <summary>An update through the public entry point, whose index the guard builds from the context's baseline and log.</summary>
    public static ContentAdmission AdmitFromBaseline(this ManagedDocumentContentGuard guard, DocumentContentContext context, byte[] update)
        => guard.AdmitUpdateAsync(context, update, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <summary>The reserved node names of the default manifest: what the guard's indexes track attributes for.</summary>
    public static IReadOnlySet<string> Reserved { get; } = YjsContentPolicy.CreateDefault().ReservedNodes;

    public static YjsDocumentIndex Index(params byte[][] trusted)
    {
        var index = new YjsDocumentIndex(Reserved, tree: true);
        foreach (var payload in trusted) index.ApplyTrusted(payload, 4L * 1024 * 1024);
        return index;
    }

    /// <summary>
    /// Corpus payloads the full guard refuses although the observer oracle (names and attribute keys only) accepted them: the
    /// spike's generator changed attribute values to ones the editor never writes and refuses on load ("&lt;value&gt; edited",
    /// a "#ffeeaa" cell colour, secret ids that are not UUIDs). Typing into a paragraph whose refused alignment the replay
    /// applied anyway re-validates that paragraph, and final states hold the refused values.
    /// </summary>
    public static IReadOnlyDictionary<string, ContentRejectionReason> RefusedValues { get; } = new Dictionary<string, ContentRejectionReason>(StringComparer.Ordinal)
    {
        ["editing/034-cell_background.update.bin"] = ContentRejectionReason.InvalidAttributeValue,
        ["editing/038-final.state.bin"] = ContentRejectionReason.InvalidAttributeValue,
        ["hostile/004-secretInlineReference_deep_in_table_cell.update.bin"] = ContentRejectionReason.InvalidAttributeValue,
        ["hostile/005-secret_nested_inside_new_blockquote_subtree.update.bin"] = ContentRejectionReason.InvalidAttributeValue,
        ["node-callout/002-attr_callout_variant.update.bin"] = ContentRejectionReason.InvalidAttributeValue,
        ["node-callout/007-final.state.bin"] = ContentRejectionReason.InvalidAttributeValue,
        ["node-concealedContent/002-attr_concealedContent_reveal.update.bin"] = ContentRejectionReason.InvalidAttributeValue,
        ["node-concealedContent/007-final.state.bin"] = ContentRejectionReason.InvalidAttributeValue,
        ["node-paragraph/002-attr_paragraph_textAlign.update.bin"] = ContentRejectionReason.InvalidAttributeValue,
        ["node-paragraph/003-type_into_paragraph_0.update.bin"] = ContentRejectionReason.InvalidDocument,
        ["node-paragraph/004-type_into_paragraph_1.update.bin"] = ContentRejectionReason.InvalidDocument,
        ["node-paragraph/005-type_into_paragraph_2.update.bin"] = ContentRejectionReason.InvalidDocument,
        ["node-paragraph/007-final.state.bin"] = ContentRejectionReason.InvalidAttributeValue,
        // Rich revision 7 has no disclosure block (every heading folds): a node of the captured editor the schema no longer holds.
        ["node-disclosure/001-insert_disclosure.update.bin"] = ContentRejectionReason.UnknownNode,
        ["node-disclosure/002-attr_disclosure_title.update.bin"] = ContentRejectionReason.UnknownAttribute,
        ["node-disclosure/007-final.state.bin"] = ContentRejectionReason.UnknownNode,
        ["all-nodes/000-all-allowed-nodes.state.bin"] = ContentRejectionReason.UnknownNode,
        ["all-nodes/001-with-secrets.state.bin"] = ContentRejectionReason.UnknownNode,
    };

    /// <summary>The verdict the full guard owes a corpus entry.</summary>
    public static bool ExpectAccept(CorpusEntry entry) => entry.ExpectAccept && !RefusedValues.ContainsKey(entry.File);

    /// <summary>Rejection reasons that match a manifest reason of the observer oracle.</summary>
    public static ContentRejectionReason[] ReasonsFor(string reason) => reason switch
    {
        _ when reason.StartsWith("reserved-attr:", StringComparison.Ordinal) => [ContentRejectionReason.ReservedAttribute],
        _ when reason.StartsWith("reserved:", StringComparison.Ordinal) => [ContentRejectionReason.ReservedNode],
        _ when reason.StartsWith("unknown-node:", StringComparison.Ordinal) => [ContentRejectionReason.UnknownNode],
        _ when reason.StartsWith("unknown-mark:", StringComparison.Ordinal) => [ContentRejectionReason.UnknownMark],
        _ when reason.StartsWith("unknown-attr:", StringComparison.Ordinal) => [ContentRejectionReason.UnknownAttribute],
        "embed" => [ContentRejectionReason.UnsupportedContent],
        "other-root" => [ContentRejectionReason.OtherRoot],
        "pending" => [ContentRejectionReason.ClockGap, ContentRejectionReason.UnknownReference, ContentRejectionReason.DeleteOutOfRange],
        _ => throw new ArgumentException("Unmapped manifest reason " + reason),
    };

    private static IReadOnlyList<CorpusEntry> LoadManifest()
    {
        using var manifest = JsonDocument.Parse(Read("manifest.json"));
        return manifest.RootElement.EnumerateArray()
            .Where(entry => entry.GetProperty("file").GetString() is { Length: > 0 })
            .Select(entry => new CorpusEntry(
                entry.GetProperty("scenario").GetString()!,
                entry.GetProperty("seq").GetInt32(),
                entry.GetProperty("label").GetString()!,
                entry.GetProperty("kind").GetString() == "state",
                entry.GetProperty("file").GetString()!,
                entry.GetProperty("expect").GetString() == "accept",
                entry.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "" : ""))
            .ToArray();
    }
}

internal sealed record CorpusEntry(string Scenario, int Seq, string Label, bool IsState, string File, bool ExpectAccept, string Reason);

/// <summary>Writes lib0 v1 Yjs updates by hand, so tests can craft payloads no editor would produce.</summary>
internal sealed class YUpdateBuilder
{
    private readonly List<(ulong Client, uint Clock, List<byte[]> Structs)> _clients = [];
    private readonly List<(ulong Client, uint Clock, uint Length)> _deletes = [];

    public YUpdateBuilder Structs(ulong client, uint clock, params byte[][] structs)
    {
        _clients.Add((client, clock, [.. structs]));
        return this;
    }

    public YUpdateBuilder Delete(ulong client, uint clock, uint length)
    {
        _deletes.Add((client, clock, length));
        return this;
    }

    public byte[] Build()
    {
        var output = new List<byte>();
        VarUint(output, (ulong)_clients.Count);
        foreach (var (client, clock, structs) in _clients)
        {
            VarUint(output, (ulong)structs.Count);
            VarUint(output, client);
            VarUint(output, clock);
            foreach (var item in structs) output.AddRange(item);
        }
        var byClient = _deletes.GroupBy(delete => delete.Client).ToArray();
        VarUint(output, (ulong)byClient.Length);
        foreach (var group in byClient)
        {
            VarUint(output, group.Key);
            VarUint(output, (ulong)group.Count());
            foreach (var delete in group)
            {
                VarUint(output, delete.Clock);
                VarUint(output, delete.Length);
            }
        }
        return [.. output];
    }

    /// <summary>Where an item integrates: explicit root or parent (with optional key), or neighbours.</summary>
    public sealed record At(string? Root = null, (ulong Client, uint Clock)? Parent = null, string? Key = null,
        (ulong Client, uint Clock)? Origin = null, (ulong Client, uint Clock)? Right = null)
    {
        public static At InRoot(string root = "default") => new(Root: root);
        public static At Under(ulong client, uint clock, string? key = null) => new(Parent: (client, clock), Key: key);
        public static At After(ulong client, uint clock) => new(Origin: (client, clock));
    }

    public static byte[] Element(At at, string name) => Item(7, at, body => { VarUint(body, 3); String(body, name); });
    public static byte[] Text(At at) => Item(7, at, body => VarUint(body, 6));
    public static byte[] Type(At at, byte typeRef) => Item(7, at, body => VarUint(body, typeRef));
    public static byte[] Str(At at, string text) => Item(4, at, body => String(body, text));
    public static byte[] AnyString(At at, string value) => Item(8, at, body => { VarUint(body, 1); body.Add(119); String(body, value); });
    public static byte[] Format(At at, string key, string json) => Item(6, at, body => { String(body, key); String(body, json); });
    public static byte[] Embed(At at, string json) => Item(5, at, body => String(body, json));
    public static byte[] Binary(At at) => Item(3, at, body => { VarUint(body, 2); body.Add(1); body.Add(2); });
    public static byte[] Json(At at) => Item(2, at, body => { VarUint(body, 1); String(body, "\"x\""); });
    public static byte[] Doc(At at) => Item(9, at, body => { String(body, "guid"); body.Add(118); VarUint(body, 0); });
    public static byte[] Deleted(At at, uint length) => Item(1, at, body => VarUint(body, length));
    public static byte[] Gc(uint length) => [0, .. VarUint(length)];
    public static byte[] Skip(uint length) => [10, .. VarUint(length)];

    public static byte[] Item(byte content, At at, Action<List<byte>> body)
    {
        var output = new List<byte>();
        var info = content;
        if (at.Origin is not null) info |= 0x80;
        if (at.Right is not null) info |= 0x40;
        var explicitParent = at.Origin is null && at.Right is null;
        if (explicitParent && at.Key is not null) info |= 0x20;
        output.Add(info);
        if (at.Origin is { } origin) { VarUint(output, origin.Client); VarUint(output, origin.Clock); }
        if (at.Right is { } right) { VarUint(output, right.Client); VarUint(output, right.Clock); }
        if (explicitParent)
        {
            if (at.Root is not null) { VarUint(output, 1); String(output, at.Root); }
            else { VarUint(output, 0); VarUint(output, at.Parent!.Value.Client); VarUint(output, at.Parent.Value.Clock); }
            if (at.Key is not null) String(output, at.Key);
        }
        body(output);
        return [.. output];
    }

    public static byte[] VarUint(ulong value)
    {
        var output = new List<byte>();
        VarUint(output, value);
        return [.. output];
    }

    public static void VarUint(List<byte> output, ulong value)
    {
        while (value >= 0x80)
        {
            output.Add((byte)(value | 0x80));
            value >>= 7;
        }
        output.Add((byte)value);
    }

    public static void String(List<byte> output, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        VarUint(output, (ulong)bytes.Length);
        output.AddRange(bytes);
    }
}
