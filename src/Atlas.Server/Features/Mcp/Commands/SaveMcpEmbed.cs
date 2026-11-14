using Atlas.Server.Features.Attachments.Commands;

namespace Atlas.Server.Features.Mcp.Commands;

/// <summary>atlas_update_embed: the shared embed change with G5.</summary>
public sealed record SaveMcpEmbed(UpdatePageEmbed Edit);
