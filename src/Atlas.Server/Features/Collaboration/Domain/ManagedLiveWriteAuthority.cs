using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;

namespace Atlas.Server.Features.Collaboration.Domain;

/// <summary>
/// The document authority in managed code (3.6: the server runs no Node.js). <see cref="YjsDocumentWriter"/> is y-prosemirror's
/// prosemirrorToYDoc and updateYFragment ported to Atlas.Yjs (parity with the editor's Yjs: tests/Atlas.Yjs.Tests and
/// ManagedLiveWriteAuthorityTests), <see cref="YjsDocumentMaterializer"/> reads a state back as the editor's JSON, and every state it
/// hands out is validated and projected as every other write (<see cref="Rules.ValidateDocument(string?)"/>). The Node worker's limits
/// stay: two documents at a time, a deadline of 10 s (checked between the steps; one step is bounded by the byte and item budgets), the
/// state, update and seed budgets. Thread-safe.
/// </summary>
public sealed class ManagedLiveWriteAuthority : ILiveWriteAuthority
{
    private const long MaxStateBytes = 16L * 1024 * 1024;
    private const int MaxUpdates = 4096;
    private const int MaxSeedBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    // Per authority (one per host): two documents at a time, as the Node worker's slots.
    private readonly SemaphoreSlim m_slots = new(2);

    private readonly ConcurrentDictionary<string, Engine> m_engines = new(StringComparer.Ordinal);

    public Task<DocSnapshot> CheckpointAsync(MarkdownDocumentSchema schema, byte[] snapshot, IReadOnlyList<byte[]> updates, long sequence, CancellationToken ct)
        => RunAsync(token =>
        {
            var engine = For(schema, updates);
            var state = updates.Count == 0 ? snapshot : YjsUpdateMerger.Merge([snapshot, .. updates.Select(update => (ReadOnlyMemory<byte>)update)], MaxStateBytes);
            token.ThrowIfCancellationRequested();
            return Snapshot(engine, state, sequence, token);
        }, ct);

    public Task<byte[]> WriteAsync(MarkdownDocumentSchema schema, byte[] snapshot, IReadOnlyList<byte[]> updates, JsonElement target, long sequence,
        CancellationToken ct)
        => RunAsync(token =>
        {
            var engine = For(schema, updates);
            // The target as the editor reads it (attribute defaults, joined text, mark order), validated as page content.
            var expected = Validated(engine, engine.Writer.Seed(target), token).Json;
            token.ThrowIfCancellationRequested();
            var written = engine.Writer.Write(snapshot, updates.Select(update => (ReadOnlyMemory<byte>)update), target);
            token.ThrowIfCancellationRequested();
            // The written document must read back as exactly the target, or the update would store something else than was asked.
            if (engine.Reader.Materialize(written.State) != expected)
            {
                throw new InvalidDataException("[INVALID_CONTENT] Live write does not reproduce its target.");
            }
            return written.Update;
        }, ct);

    public Task<DocSnapshot> SeedAsync(MarkdownDocumentSchema schema, JsonElement document, CancellationToken ct)
        => RunAsync(token =>
        {
            var engine = For(schema, []);
            var raw = document.GetRawText();
            if (raw.Length > MaxSeedBytes || JsonNode.Parse(raw) is not JsonObject stored)
            {
                throw new InvalidDataException("[DOCUMENT_FULL] Authority seed exceeds its budget.");
            }
            using var seed = JsonDocument.Parse(BrowserSeed.Prepare(stored).ToJsonString());
            var state = engine.Writer.Seed(seed.RootElement);
            token.ThrowIfCancellationRequested();
            return Snapshot(engine, state, 0, token);
        }, ct);

    /// <summary>One request in a slot, within the deadline; past it [AUTHORITY_TIMEOUT], as the Node worker answered.</summary>
    private async Task<T> RunAsync<T>(Func<CancellationToken, T> work, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Deadline);
        try
        {
            await m_slots.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                return work(deadline.Token);
            }
            finally
            {
                m_slots.Release();
            }
        }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        {
            throw new InvalidDataException("[AUTHORITY_TIMEOUT] The authority's deadline elapsed; retry the same update identity.", error);
        }
    }

    private Engine For(MarkdownDocumentSchema schema, IReadOnlyList<byte[]> updates)
    {
        if (updates.Count > MaxUpdates)
        {
            throw new InvalidDataException("[DOCUMENT_FULL] Too many authority updates.");
        }
        // By plugin ids alone: the host has one manifest, at its plugins' current versions, which reads every version it accepts
        // (CollaborationSchema.KnownHostSchemas), as the content guard reads them; a version changes nothing the engine builds.
        var plugins = DocumentSchemaCompatibility.Normalize(schema).Plugins.Select(plugin => plugin.Id).Order(StringComparer.Ordinal).ToArray();
        return m_engines.GetOrAdd(string.Join(',', plugins), _ =>
        {
            var manifest = HostDocumentValidator.Instance.Schema.WithPlugins(plugins);
            return new Engine(new YjsDocumentWriter(manifest, maxPayloadBytes: MaxStateBytes), new YjsDocumentMaterializer(manifest, maxPayloadBytes: MaxStateBytes));
        });
    }

    private static DocSnapshot Snapshot(Engine engine, byte[] state, long sequence, CancellationToken ct)
    {
        var (json, markdown) = Validated(engine, state, ct);
        using var editorJson = JsonDocument.Parse(json);
        // Plain text is the host's to derive, as for the server's compaction (FlushDocument stores neither projection of a snapshot).
        return SnapshotValidation.Prepare(new DocSnapshot(state, sequence,
            new ProjectionSetDto(sequence, Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant(), markdown, editorJson.RootElement, "")));
    }

    private static (string Json, string Markdown) Validated(Engine engine, byte[] state, CancellationToken ct)
    {
        if (state.Length > MaxStateBytes)
        {
            throw new InvalidDataException("[DOCUMENT_FULL] Authority state exceeds its budget.");
        }
        var json = engine.Reader.Materialize(state);
        ct.ThrowIfCancellationRequested();
        var (_, markdown) = Rules.ValidateDocument($"{{\"format\":\"{MarkdownDocumentValidator.Format}\",\"version\":1,\"plugins\":{Commands.FlushDocumentHandler.Plugins},\"document\":{json}}}");
        return (json, markdown);
    }

    private sealed record Engine(YjsDocumentWriter Writer, YjsDocumentMaterializer Reader);
}
