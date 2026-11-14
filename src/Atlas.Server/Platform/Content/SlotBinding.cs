using System.Text.Json.Nodes;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// The original node behind placeholder <see cref="Index"/> and its JSON Pointer in the document (<c>/content/3/content/0</c>).
/// Server only: the merge (K2) puts the node back unchanged; it never goes to a client.
/// </summary>
internal sealed record SlotBinding(int Index, JsonNode Original, string Path);
