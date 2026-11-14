namespace Atlas.Server.Features.ContentExport.Contracts;

public sealed record ExportFile(string Path, long Size, string Sha256, string MediaType);
