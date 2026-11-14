namespace Atlas.Server.Features.ContentExport.Contracts;

public sealed record ExportPage(Guid Id, Guid SpaceId, Guid? ParentId, int SortOrder, string CanonicalUrl, Guid RevisionId, int Version, string Title, DateTime ActiveFrom, DateTime? ActiveTo, bool IsPublished, string ContentJsonPath, string MarkdownPath);
