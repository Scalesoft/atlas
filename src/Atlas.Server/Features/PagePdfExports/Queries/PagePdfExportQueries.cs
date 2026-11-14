namespace Atlas.Server.Features.PagePdfExports.Queries;

/// <summary>Authorizes browsing a profile from a page's export settings and returns the normalized folder.</summary>
public sealed record AuthorizePagePdfExportFolder(Guid PageId, Guid StorageProfileId, string Folder);
/// <summary>The validated target of a save when it is not a file this export manages (null: managed, disabled, or claimed by another page).</summary>
public sealed record FindUnmanagedPagePdfExportTarget(Guid PageId, PagePdfExportRequest Request);
/// <summary>Every page's automatic PDF for the storage overview (wiki administrator).</summary>
public sealed record ListPagePdfExports;
