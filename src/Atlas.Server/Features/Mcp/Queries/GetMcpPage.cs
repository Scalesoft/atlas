namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>atlas_get_page: the publication by default, also for an editor (a page in Živě mode: its live state, as REST); the working revision only with EditContent.
/// <paramref name="RecordOpening"/> false for a caller that records the one opening of its call itself.</summary>
public sealed record GetMcpPage(Guid PageId, bool Working, bool RecordOpening = true);
