using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.Attachments.Commands;

/// <summary>
/// Change the look of the embed of <see cref="AttachmentId"/> and/or show <see cref="ReplacementId"/> in its place, in a page's working
/// revision at <see cref="ExpectedVersion"/> (REST and MCP alike).
/// </summary>
public sealed record UpdatePageEmbed(Guid PageId, int ExpectedVersion, Guid AttachmentId, int? Occurrence, ImageLook Look, Guid? ReplacementId);

/// <summary>The REST command of an embed change: <see cref="EditPageEmbedHandler"/> in one transaction.</summary>
public sealed record EditPageEmbed(UpdatePageEmbed Edit);
