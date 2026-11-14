using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.Collaboration.Domain;

/// <summary>
/// atlas-requirements 3 → 4 in a live document (shape §6). OpenCollaboration moves a live state of a previous host schema, unsaved edits
/// included, to a new generation unchanged (<see cref="CollaborationSchema.UpgradeKnownSchema"/>); its requirements still name their party
/// in the facts line. After the commit, the server moves it as the editor's own migration does (<see cref="RequirementHeadings.MoveParty"/>),
/// as one live write of the channel system, so MCP and readers read version 4 before any editor loads the document. MoveParty moves nothing
/// on a version-4 document and only sets attributes and removes the facts line, so this and the web's migration converge.
/// Every update names an account (CollaborationUpdate.UserId): the one whose unsaved edits it converts; the channel says it was the system.
/// </summary>
public sealed class LiveRequirementsUpgrade(LiveDocumentWriter writer, ILiveWriteAuthority authority, ILogger<LiveRequirementsUpgrade> log)
{
    private static readonly YjsDocumentMaterializer Host = new(HostDocumentValidator.Instance.Schema, maxPayloadBytes: 16L * 1024 * 1024);

    /// <returns>True when it appended the conversion.</returns>
    public async Task<bool> ConvertAsync(string documentId, CancellationToken ct)
    {
        try
        {
            await using var hold = await writer.HoldAsync(documentId, ct);
            if (hold is null)
            {
                return false;
            }
            var document = JsonNode.Parse(Host.Materialize(hold.Snapshot, hold.Updates.Select(update => (ReadOnlyMemory<byte>)update).ToArray()))!.AsObject();
            var schema = hold.Schema ?? CollaborationSchema.KnownHostSchemas[^1];
            if (hold.AuthorAfter(0) is not { } author || document["content"] is not JsonArray content || !RequirementHeadings.MoveParty(content))
            {
                // The stored projection is the previous editor's, which this editor's save never equals (collaboration_conflict in the
                // 3.5.2 → 3.6 upgrade test): the head is projected again as this host reads it. A conversion appends an update instead,
                // whose snapshot the snapshotter pushes.
                await hold.AdoptAsync(await authority.CheckpointAsync(schema, hold.Snapshot, hold.Updates, hold.Sequence, ct), ct);
                return false;
            }
            using var target = JsonDocument.Parse(document.ToJsonString());
            var update = await authority.WriteAsync(schema, hold.Snapshot, hold.Updates, target.RootElement, hold.Sequence + 1, ct);
            await hold.AppendAsync(new CollabUser(author, "Atlas"), update, new UpdateOrigin(RequestChannel.System.Code), ct);
            log.LogInformation("Live document {DocumentId} moved to atlas-requirements 4 (the party in the heading)", documentId);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The editor's own migration converts it on load; nothing is lost either way.
            log.LogWarning(error, "The server could not move live document {DocumentId} to atlas-requirements 4; its editor does", documentId);
            return false;
        }
    }
}
