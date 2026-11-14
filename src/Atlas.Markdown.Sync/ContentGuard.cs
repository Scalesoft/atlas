using System.Text.Json;

namespace Atlas.Markdown.Sync;

/// <summary>
/// Admission of collaborative Yjs content. The hub calls it under the per-document gate before a client update is
/// appended and before a pushed snapshot (including the initial seed) is adopted; a rejection is reported to the
/// sender as <c>[INVALID_CONTENT]</c> and nothing is stored or broadcast. The default registration admits
/// everything (opaque relay); <see cref="ServiceCollectionExtensions.AddAtlasMarkdownContentGuard"/> enables
/// <see cref="ManagedDocumentContentGuard"/>.
/// </summary>
public interface IDocumentContentGuard
{
    ValueTask<ContentAdmission> AdmitUpdateAsync(DocumentContentContext context, ReadOnlyMemory<byte> update, CancellationToken ct);

    ValueTask<ContentAdmission> AdmitSnapshotAsync(DocumentContentContext context, ReadOnlyMemory<byte> state, CancellationToken ct);
}

/// <summary>What the hub knows about the document when it asks for admission. Valid only for the duration of the call.</summary>
/// <param name="DocumentId">The collaborative document.</param>
/// <param name="AuthorUserId">The user whose connection sent the payload.</param>
/// <param name="PeerUserIds">Distinct users currently joined to the document (readers and writers, author included).</param>
/// <param name="Schema">The bound document schema, if any.</param>
/// <param name="Baseline">The admitted full state, or null for an unseeded document.</param>
/// <param name="Log">Admitted updates above <paramref name="Baseline"/>, in sequence order.</param>
/// <param name="Session">
/// Identity of the hub's in-memory baseline. A new instance means the baseline was replaced (reload, reseed) or an
/// admitted payload did not become durable: anything a guard cached for an older session must not be used.
/// </param>
public sealed record DocumentContentContext(
    string DocumentId,
    string AuthorUserId,
    IReadOnlyList<string> PeerUserIds,
    MarkdownDocumentSchema? Schema,
    byte[]? Baseline,
    IReadOnlyList<LogUpdate> Log,
    object Session);

/// <summary>
/// Host decision for reserved nodes (for example secret references) that a payload creates or whose attributes it
/// writes. Without a registered implementation the managed guard rejects every reserved node change.
/// </summary>
public interface IReservedNodeGuard
{
    /// <summary>True admits every change of the request; false rejects the whole payload.</summary>
    ValueTask<bool> AdmitAsync(ReservedNodeRequest request, CancellationToken ct);
}

/// <summary>All reserved node changes of one payload, with the author and the users who would receive it.</summary>
public sealed record ReservedNodeRequest(
    string DocumentId,
    string AuthorUserId,
    IReadOnlyList<string> PeerUserIds,
    IReadOnlyList<ReservedNodeChange> Changes)
{
    /// <summary>
    /// Where the changes come from: a client update, a pushed snapshot of a seeded document, or the seed of an unseeded
    /// one. A snapshot or seed carries the whole document, so every reserved node in it is reported as new
    /// (<see cref="ReservedNodeChange.IsNewNode"/>); for a snapshot, <see cref="ReservedNodeChange.CurrentAttributes"/>
    /// tells a node the server already admitted from a genuinely new one.
    /// </summary>
    public ReservedNodePayload Payload { get; init; } = ReservedNodePayload.Update;
}

/// <summary>The kind of payload a <see cref="ReservedNodeRequest"/> is about.</summary>
public enum ReservedNodePayload
{
    /// <summary>An incremental client update appended to the document.</summary>
    Update,

    /// <summary>A full state pushed for a document that already has admitted content.</summary>
    Snapshot,

    /// <summary>The first full state of an unseeded document; nothing was admitted before it.</summary>
    Seed,
}

/// <summary>A reserved node the payload creates (<paramref name="IsNewNode"/>) or whose attributes it rewrites.</summary>
/// <param name="Attributes">Attribute values the payload writes for that node, as JSON (the last write of a key wins).</param>
public sealed record ReservedNodeChange(string NodeName, bool IsNewNode, IReadOnlyDictionary<string, JsonElement> Attributes)
{
    private static readonly IReadOnlyDictionary<string, JsonElement> NoAttributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    /// <summary>
    /// The node's attribute values before this payload, as the server last admitted them: filled for an attribute
    /// rewrite of an existing node and for a node a snapshot sends again, empty for a node the payload creates and for
    /// a seed. Compare it with <see cref="Attributes"/> to check both the old and the new value (for example the old and
    /// the new <c>secretId</c>). Under concurrent writes of one key, Yjs may settle on another admitted value than the
    /// last one the server saw.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> CurrentAttributes { get; init; } = NoAttributes;

    /// <summary>The current <c>secretId</c> of a secret reference (from <see cref="CurrentAttributes"/>), or null.</summary>
    public string? CurrentSecretId => CurrentAttributes.TryGetValue("secretId", out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;
}

/// <summary>The default guard: the relay stays opaque and admits every payload.</summary>
internal sealed class PassThroughDocumentContentGuard : IDocumentContentGuard
{
    public ValueTask<ContentAdmission> AdmitUpdateAsync(DocumentContentContext context, ReadOnlyMemory<byte> update, CancellationToken ct)
        => ValueTask.FromResult(ContentAdmission.Accepted);

    public ValueTask<ContentAdmission> AdmitSnapshotAsync(DocumentContentContext context, ReadOnlyMemory<byte> state, CancellationToken ct)
        => ValueTask.FromResult(ContentAdmission.Accepted);
}
