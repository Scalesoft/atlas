namespace Atlas.Server.Features.ContentExport.Contracts;

public sealed record ExportSelection(Guid PageId, Guid SpaceId, Guid RevisionId);
