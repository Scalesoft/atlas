using Atlas.Server.Features.PageLinks.Contracts;

namespace Atlas.Server.Features.Mcp.Contracts;

/// <summary>
/// What the assistant's card shows about a publication before it happens: the page, its working and published version, who
/// changed it last and how, how many sections differ from the publication and the link findings of the check before publication
/// (P5). Counts and names only, never page text.
/// </summary>
public sealed record McpPublishSummary(Guid PageId, Guid SpaceId, string Title, string SpaceName, int Version, int? PublishedVersion,
    bool FirstPublication, int ChangedSections, string? LastAuthorName, string? LastChannel, PublicationCheck Findings);

/// <summary>atlas_publish: the page after the publication; Published is false when that version was already published.</summary>
public sealed record McpPublishOutcome(Guid PageId, Guid SpaceId, int Version, int? PublishedVersion, bool Published);

