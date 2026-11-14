namespace Atlas.Server.Features.Pages.Contracts;

/// <summary>As <see cref="SavePageRequest"/>: the stored Markdown is written from <paramref name="ContentJson"/>, <paramref name="Markdown"/> is ignored.</summary>
public sealed record CreatePageRequest(Guid SpaceId, Guid? ParentId, string Title, string ContentJson, string? Markdown,
    bool IsTemplate = false, Guid? TemplatePageId = null);
