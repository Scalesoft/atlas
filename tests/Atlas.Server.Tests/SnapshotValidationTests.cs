using System.Security.Cryptography;
using System.Text.Json;
using Atlas.Markdown.Sync;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class SnapshotValidationTests
{
    [Fact]
    public void Prepare_rejects_snapshot_with_mismatched_state_hash()
    {
        var snapshot = Snapshot([1, 2, 3], "00", 4, 4);

        Assert.Throws<InvalidDataException>(() => SnapshotValidation.Prepare(snapshot));
    }

    [Fact]
    public void Prepare_rejects_projection_from_a_different_sequence()
    {
        byte[] state = [1, 2, 3];
        var snapshot = Snapshot(state, Hash(state), 4, 3);

        Assert.Throws<InvalidDataException>(() => SnapshotValidation.Prepare(snapshot));
    }

    [Fact]
    public void Prepare_returns_an_owned_snapshot_with_a_document_projection()
    {
        byte[] state = [1, 2, 3];
        using var source = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[]}");
        var snapshot = new DocSnapshot(state, 4,
            new ProjectionSetDto(4, Hash(state), "Markdown", source.RootElement, "Plain text"));

        var prepared = SnapshotValidation.Prepare(snapshot);
        state[0] = 9;
        source.Dispose();

        Assert.Equal(new byte[] { 1, 2, 3 }, prepared.YjsState);
        Assert.Equal("doc", prepared.Projections.EditorJson.GetProperty("type").GetString());
    }

    [Fact]
    public void Prepare_rejects_an_editor_projection_that_is_not_a_document()
    {
        byte[] state = [1];
        using var source = JsonDocument.Parse("{\"type\":\"paragraph\"}");
        var snapshot = new DocSnapshot(state, 1,
            new ProjectionSetDto(1, Hash(state), "Markdown", source.RootElement.Clone(), "Plain text"));

        Assert.Throws<InvalidDataException>(() => SnapshotValidation.Prepare(snapshot));
    }

    private static DocSnapshot Snapshot(byte[] state, string hash, long coveredSequence, long sourceSequence)
    {
        using var source = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[]}");
        return new(state, coveredSequence,
            new(sourceSequence, hash, "Markdown", source.RootElement.Clone(), "Plain text"));
    }

    private static string Hash(byte[] state)
        => Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant();
}