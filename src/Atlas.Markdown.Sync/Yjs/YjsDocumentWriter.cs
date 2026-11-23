using System.Security.Cryptography;
using System.Text.Json;
using Atlas.Yjs.ProseMirror;

namespace Atlas.Markdown.Sync;

/// <summary>
/// Writes ProseMirror documents into the editor's Yjs document without JavaScript, byte for byte as the editor's binding writes them
/// (Atlas.Yjs: yjs 13.6.33 and @tiptap/y-tiptap 3.0.9 ported): <see cref="Seed"/> is prosemirrorToYDoc, <see cref="Write"/> the
/// server's live write (updateYFragment of the live document towards a target, so unchanged blocks keep their items and concurrent
/// edits of other blocks merge). The target is normalized as schema.nodeFromJSON normalizes it; its content is not validated here
/// (the caller validates it with <see cref="YjsDocumentMaterializer"/> and <see cref="MarkdownDocumentValidator"/>). Immutable and
/// thread-safe.
/// </summary>
public sealed class YjsDocumentWriter
{
    private readonly PmSchema _schema;
    private readonly string _root;
    private readonly long _maxPayloadBytes;

    /// <param name="schema">The editor configuration's schema.</param>
    /// <param name="root">The fragment the editor binds (the Collaboration extension's field).</param>
    /// <param name="maxPayloadBytes">The bound of every payload read and of the update written, the host's <see cref="MarkdownSyncOptions.MaxDocumentBytes"/>.</param>
    public YjsDocumentWriter(MarkdownSchemaManifest schema, string root = "default", long maxPayloadBytes = 4L * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadBytes);
        static PmAttribute Attribute(SchemaAttribute attribute) => new(attribute.Name, attribute.Default is not null, attribute.Default);
        _schema = new PmSchema(schema.TopNode,
            schema.Nodes.Values.Where(node => !node.IsText).ToDictionary(node => node.Name, node => new PmNodeType(node.Name, [.. node.Declared.Select(Attribute)]), StringComparer.Ordinal),
            schema.Marks.Values.ToDictionary(mark => mark.Name, mark => new PmMarkType(mark.Name, mark.Rank, mark.Excludes.Contains(mark.Name), [.. mark.Declared.Select(Attribute)]), StringComparer.Ordinal),
            MarkdownDocumentValidator.MaxDepth);
        _root = root;
        _maxPayloadBytes = maxPayloadBytes;
    }

    /// <summary>
    /// prosemirrorToYDoc: Y.encodeStateAsUpdate of a fresh Y.Doc whose fragment updateYFragment filled with <paramref name="document"/>
    /// (a ProseMirror <c>doc</c> node as getJSON() writes it).
    /// </summary>
    /// <param name="clientId">The new document's client; random when null.</param>
    /// <exception cref="YjsWriteException">An unknown node or mark, a missing required attribute, or a state beyond the payload bound.</exception>
    public byte[] Seed(JsonElement document, uint? clientId = null)
        => Run(() => Bounded(YProseMirror.ProsemirrorToYDoc(Target(document), _root, clientId ?? (uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue))));

    /// <summary>
    /// updateYFragment of the live document (<paramref name="state"/> and the <paramref name="updates"/> after it, as admitted) towards
    /// <paramref name="target"/>: the update (Y.encodeStateAsUpdate(doc, stateVectorBefore)) and the full state after it.
    /// </summary>
    /// <param name="clientId">The writing client; when null, or one the document already has structs of, a random one it does not know.</param>
    /// <exception cref="YjsWriteException">A payload is malformed or incomplete, the target cannot be written, or a bound is exceeded.</exception>
    public YjsWrite Write(ReadOnlyMemory<byte> state, IEnumerable<ReadOnlyMemory<byte>> updates, JsonElement target, uint? clientId = null)
        => Run(() =>
        {
            ArgumentNullException.ThrowIfNull(updates);
            var payloads = (ReadOnlyMemory<byte>[])[state, .. updates];
            var tree = new YjsDocumentTree(text: true);
            long bytes = 0;
            foreach (var payload in payloads)
            {
                if (payload.IsEmpty) continue;
                tree.Apply(payload.Span, YjsUpdateDecoder.Decode(payload.Span, _maxPayloadBytes), journal: false, bounded: false);
                bytes += payload.Length;
            }
            var pNode = Target(target);
            var client = clientId is { } chosen && tree.GetState(chosen) == 0 ? chosen : NewClient(tree);
            var y = new YDocument(tree, client);
            YProseMirror.UpdateYFragment(y, y.GetXmlFragment(_root), pNode);
            var update = Bounded(y.EncodeUpdate());
            if (tree.ItemCount > YjsLimits.ItemBudget(bytes + update.Length))
                throw new ContentRejectedException(ContentRejectionReason.TooManyItems, $"the document would hold {tree.ItemCount} items");
            var written = payloads.Where(payload => !payload.IsEmpty).Append(update).ToArray();
            return new YjsWrite(update, Atlas.Yjs.YjsUpdateMerger.Merge(written, Math.Max(_maxPayloadBytes, update.Length)));
        });

    private PmNode Target(JsonElement document)
    {
        var node = PmNode.FromJson(_schema, document);
        if (node.TypeName != _schema.TopNode)
            throw new ContentRejectedException(ContentRejectionReason.InvalidStructure, $"the document is a {node.TypeName}, not a {_schema.TopNode}");
        return node;
    }

    private byte[] Bounded(byte[] payload) => payload.Length <= _maxPayloadBytes ? payload
        : throw new ContentRejectedException(ContentRejectionReason.LimitExceeded, $"the written payload exceeds {_maxPayloadBytes} bytes");

    /// <summary>Y.Doc's random uint32 clientID, one the document has no structs of.</summary>
    private static uint NewClient(YjsDocumentTree tree)
    {
        while (true)
        {
            var client = (uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);
            if (tree.GetState(client) == 0) return client;
        }
    }

    private static T Run<T>(Func<T> write)
    {
        try
        {
            return write();
        }
        catch (ContentRejectedException rejection)
        {
            throw new YjsWriteException(rejection.Reason, rejection.Detail);
        }
    }
}

/// <summary>A live write: the update to store and broadcast, and the full state after it.</summary>
public sealed record YjsWrite(byte[] Update, byte[] State);

/// <summary>A document <see cref="YjsDocumentWriter"/> cannot write: <see cref="Reason"/> says why, as the content guard names it.</summary>
public sealed class YjsWriteException(ContentRejectionReason reason, string detail) : Exception($"{reason}: {detail}")
{
    public ContentRejectionReason Reason { get; } = reason;

    public string Detail { get; } = detail;
}
