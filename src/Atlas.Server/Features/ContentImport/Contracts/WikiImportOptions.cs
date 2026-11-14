namespace Atlas.Server.Features.ContentImport.Contracts;

public sealed record WikiImportOptions(string SourceKey, string SpaceName, string? StripPrefix = null, string? SourceBaseUrl = null, bool UpdateExisting = false);
