namespace Atlas.Server.Features.ContentExport.Queries;

public sealed record BuildContentExport(Guid Id, bool WholeSpace, Guid? RevisionId = null);
