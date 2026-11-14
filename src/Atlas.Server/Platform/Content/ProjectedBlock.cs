using System.Text.Json.Nodes;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// One top-level block of a <see cref="ContentProjection"/> as the merge (K2) aligns against it: its index in the
/// document, its text form with inline placeholders (<see cref="Prepared"/>, null for a block placeholder) or the number
/// of its block placeholder (<see cref="SlotIndex"/>), and the numbers of its inline placeholders in order. Empty
/// paragraphs have no Markdown form and no entry. Server only.
/// </summary>
internal sealed record ProjectedBlock(int DocumentIndex, JsonObject? Prepared, int SlotIndex, IReadOnlyList<int> InlineSlots);
