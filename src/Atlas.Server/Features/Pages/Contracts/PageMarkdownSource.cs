using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.Pages.Contracts;

/// <summary>
/// The page as an API client edits it (<c>GET /api/v1/pages/{id}?format=markdown</c>): the editor's Markdown dialect in which every
/// node Markdown cannot carry is a numbered placeholder <c>⟦c:N⟧</c> described in <see cref="Components"/>; images of the page's own
/// attachments are Markdown (<c>![popis](/api/attachments/{id}/content)</c>). Sending it back to <c>PUT /api/v1/pages/{id}</c> with the
/// page's version changes nothing; a placeholder brings its node back, a placeholder left out removes its component when the save
/// lists it in <c>removeComponents</c>.
/// </summary>
public sealed record PageMarkdownSource(string Markdown, IReadOnlyList<ComponentSlot> Components, IReadOnlyList<string> Warnings)
{
    /// <summary>The projection of the revision the page answer carries; null for content that is no editor document.</summary>
    public static PageMarkdownSource? From(PageDto page)
    {
        RichDocument document;
        try
        {
            document = RichDocument.Parse(page.ContentJson);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        var projection = RichMarkdownProjection.Project(document, new ProjectionOptions(page.Version));
        return new PageMarkdownSource(projection.Markdown, projection.Components, projection.Warnings);
    }
}
