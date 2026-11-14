namespace Atlas.Server.Features.PageLinks.Queries;

/// <summary>Resolves rendered page links in one read, without recording page openings (page.opened belongs to GET /api/pages/{id}).</summary>
public sealed record ResolvePageReferences(IReadOnlyList<Guid> Ids);
