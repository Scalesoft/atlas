namespace Atlas.Server.Features.Mcp.Commands;

/// <summary>
/// The assistant's preview of a publication (WP-41, spec §6.3): the checks of a publication (EditContent, G5, the expected version, nobody's
/// unsaved live edits) and the summary of <see cref="Publishing.McpPublishSummaries"/>, without a token. 409
/// <c>already_published</c> when that version is the publication already. Never publishes.
/// </summary>
public sealed record PreviewMcpPublish(Guid PageId, int ExpectedVersion, string? ChangeSummary);

/// <summary>The assistant's publication of a proposal its owner confirmed on the card (WP-41): only on the channel assistant with the
/// approved-proposal marker, else 403 <c>publish_confirmation_required</c>; then the shared <c>PublishPageHandler</c>.</summary>
public sealed record PublishMcpApproved(Guid PageId, int ExpectedVersion, string? ChangeSummary);

/// <summary>atlas_publish of a token with the scope write (3.6; formerly direct): publishes at once, without a request or the web
/// confirmation; only on the channel mcp with that scope in effect, else 403 <c>publish_confirmation_required</c>.</summary>
public sealed record PublishMcpDirect(Guid PageId, int ExpectedVersion, string? ChangeSummary);
