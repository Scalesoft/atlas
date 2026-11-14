namespace Atlas.Server.Features.Pages.Contracts;

/// <summary>
/// POST /api/v1/pages: a draft written as Markdown in the editor's dialect (<paramref name="Markdown"/>, empty for an empty page); the
/// server converts it. With <paramref name="TemplatePageId"/> the page starts as the template's copy and the Markdown is appended.
/// <paramref name="ContentJson"/> exists only to be refused (400 <c>content_json_not_accepted</c>): the rich document is the web
/// editor's alone.
/// </summary>
/// <remarks><c>Link</c> <c>auto</c> (default): names of people the Markdown writes in full become chips (several people of one name an
/// unresolved chip); the answer's <c>links</c> lists them and what is only offered. <c>off</c> links nothing. The same in the save.</remarks>
public sealed record ApiCreatePageRequest(Guid SpaceId, Guid? ParentId, string Title, string? Markdown = null, bool IsTemplate = false,
    Guid? TemplatePageId = null, string? ContentJson = null, string? Link = null);

/// <summary>
/// PUT /api/v1/pages/{id}: the whole working revision as Markdown with the component placeholders <c>⟦c:N⟧</c> of
/// <c>GET /api/v1/pages/{id}?format=markdown</c> at <paramref name="ExpectedVersion"/>. <paramref name="Title"/> null keeps the title.
/// A placeholder left out removes its component only when <paramref name="RemoveComponents"/> lists its number (otherwise 409
/// <c>components_removed</c>). An edit that would drop stored presentation Markdown does not carry is 409 <c>formatting_lost</c> unless
/// <paramref name="ResetFormatting"/>. <paramref name="ContentJson"/> exists only to be refused, as in <see cref="ApiCreatePageRequest"/>.
/// </summary>
public sealed record ApiSavePageRequest(string? Markdown, int ExpectedVersion, string? Title = null, string? ContentJson = null,
    IReadOnlyList<int>? RemoveComponents = null, bool ResetFormatting = false, string? Link = null) : Collaboration.IExpectedVersion;
