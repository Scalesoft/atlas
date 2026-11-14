namespace Atlas.Server.Features.PagePdfExports.Commands;

/// <summary>Repeats a page's automatic PDF from the storage overview (wiki administrator); the export keeps its owner.</summary>
public sealed record RetryPagePdfExport(Guid PageId);
