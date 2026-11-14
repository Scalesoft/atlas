using Atlas.Server.Features.ContentImport.Contracts;

namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>An HTML page or block as the Markdown the converter reads, with what could not be carried over.</summary>
public sealed record ConvertedHtml(string Markdown, IReadOnlyList<WikiImportFinding> Findings);
