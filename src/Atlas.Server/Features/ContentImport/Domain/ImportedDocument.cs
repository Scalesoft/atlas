using Atlas.Server.Features.ContentImport.Contracts;

namespace Atlas.Server.Features.ContentImport.Domain;

public sealed record ImportedDocument(string ContentJson, string Markdown, IReadOnlyList<WikiImportFinding> Findings);
