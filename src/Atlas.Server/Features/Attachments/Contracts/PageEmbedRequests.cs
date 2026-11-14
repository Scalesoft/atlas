namespace Atlas.Server.Features.Attachments.Contracts;

/// <summary>
/// PATCH /pages/{id}/embeds/{attachmentId}: changes the embed of that attachment (its <paramref name="Occurrence"/>-th on the page,
/// from 1, when it is embedded more than once). The look of an image (<paramref name="Alt"/>, <paramref name="Width"/> 10–100 %, <paramref name="Align"/> <c>left</c>, <c>center</c> or <c>right</c>), and/or <paramref name="ReplaceWithAttachmentId"/>:
/// another attachment of the same page shown in its place (an image for an image).
/// </summary>
public sealed record UpdatePageEmbedRequest(int ExpectedVersion, int? Occurrence = null, string? Alt = null, int? Width = null, string? Align = null,
    Guid? ReplaceWithAttachmentId = null) : Collaboration.IExpectedVersion;
