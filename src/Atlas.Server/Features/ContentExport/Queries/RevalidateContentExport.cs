using Atlas.Server.Features.ContentExport.Contracts;

namespace Atlas.Server.Features.ContentExport.Queries;

/// <summary>WholeSpace also requires current membership of the space, whose name and appearance the manifest carries even when the space is empty.</summary>
public sealed record RevalidateContentExport(Guid SpaceId, IReadOnlyList<ExportSelection> Selected, Guid? AuthenticatedTokenId = null, bool WholeSpace = false);
