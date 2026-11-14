namespace Atlas.Server.Features.ContentImport.Contracts;

public sealed record WikiImportCounts(int Create, int Update, int Skip, int Conflict, int Unsupported, int Assets);
