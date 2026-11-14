using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.Attachments.Commands;
using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Platform.Content;
using Wolverine;

namespace Atlas.Server.Features.Attachments;

/// <summary>
/// The presentation of an embed of a page's attachments in its working revision, "on the side" of the Markdown that inserts and
/// removes images and links: an image's look, or another attachment in place of an image or a file card, without sending the rich
/// document. Answers like a save (the new version) and publishes nothing. Personal API only: the web edits the look in the editor.
/// </summary>
public static class PageEmbedEndpoints
{
    public static void MapPageEmbeds(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api");
        group.MapPatch("/pages/{id:guid}/embeds/{attachmentId:guid}",
                async (Guid id, Guid attachmentId, UpdatePageEmbedRequest request, IMessageBus bus, CancellationToken ct) =>
                    Results.Ok(await bus.InvokeAsync<PageDto>(new EditPageEmbed(new UpdatePageEmbed(id, request.ExpectedVersion, attachmentId, request.Occurrence,
                        new ImageLook(request.Alt, request.Width, request.Align), request.ReplaceWithAttachmentId)), ct)))
            .LiveWrite().Produces<PageDto>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Change the presentation of an embedded attachment (images and download links are inserted and removed as Markdown with " +
                "PUT /pages/{id}): alt, width (10–100) and align (left/center/right) of an image, or replaceWithAttachmentId (another attachment of this " +
                "page, an image for an image). occurrence (from 1) picks one when the attachment is embedded more than once. Requires expectedVersion; " +
                "everything else in the document stays as stored. Publishes nothing.")
            .RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.PageEmbedUpdate).WithName("UpdatePageEmbed");
    }
}
