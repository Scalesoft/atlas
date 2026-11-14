namespace Atlas.Server.Features.ContentImport.Queries;

public sealed record GetImportReport(Guid Id);
public sealed record RevalidateImportDownload(Guid Id, bool Source);
