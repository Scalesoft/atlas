namespace Atlas.Server.Features.Relations.Queries;

/// <param name="Depth">1: the object's own edges; 2: also the edges of its readable neighbours.</param>
/// <param name="Kinds">Comma-separated relation kinds to keep (link, placement, attached, mention…); null: all.</param>
/// <param name="Drafts">Also the uses only a page's working revision holds (Draft), to readers who may edit that page.</param>
/// <param name="Reasons">Why the object is used (PartId, Excerpt) on its own uses, the first 50 of them (plan 3.1 M5).</param>
public sealed record GetObjectRelations(Guid ObjectId, int Depth = 1, string? Kinds = null, bool Drafts = false, bool Reasons = false);
