using System.Security.Cryptography;
using System.Text.Json;

namespace Atlas.Markdown.Sync;

/// <summary>
/// Validates and takes ownership of the snapshot envelope. This does not decode Yjs or
/// prove that the supplied projections were derived from its state.
/// </summary>
public static class SnapshotValidation
{
    /// <summary>
    /// Returns a snapshot with independently owned state bytes and editor JSON.
    /// The hash retains the existing wire/storage contract: exactly 64 lowercase hex digits.
    /// </summary>
    /// <exception cref="InvalidDataException">The snapshot envelope is invalid.</exception>
    public static DocSnapshot Prepare(DocSnapshot snapshot)
    {
        if (snapshot is null) throw new InvalidDataException("Snapshot is missing.");
        if (snapshot.YjsState is null) throw new InvalidDataException("Snapshot state is missing.");
        if (snapshot.Projections is null) throw new InvalidDataException("Snapshot projections are missing.");

        // Validate the copies that will actually be published/persisted. The caller may
        // mutate its byte array or dispose its JsonDocument after this method returns.
        var state = snapshot.YjsState.ToArray();
        JsonElement editorJson;
        try { editorJson = snapshot.Projections.EditorJson.Clone(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException("Editor JSON projection is unavailable.", error);
        }

        var projections = snapshot.Projections;
        if (snapshot.CoveredSequence < 0)
            throw new InvalidDataException("Covered sequence cannot be negative.");
        if (projections.SourceSequence != snapshot.CoveredSequence)
            throw new InvalidDataException("Projection source sequence does not match the snapshot.");
        var hash = Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant();
        if (!string.Equals(hash, projections.StateSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Projection state hash does not match the snapshot.");
        if (projections.Markdown is null)
            throw new InvalidDataException("Markdown projection is missing.");
        if (projections.PlainText is null)
            throw new InvalidDataException("Plain text projection is missing.");
        if (editorJson.ValueKind != JsonValueKind.Object ||
            !editorJson.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            !string.Equals(type.GetString(), "doc", StringComparison.Ordinal))
            throw new InvalidDataException("Editor JSON projection must be a document object.");
        if (editorJson.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Editor JSON document content must be an array.");

        return new DocSnapshot(state, snapshot.CoveredSequence, projections with { EditorJson = editorJson });
    }
}
