using System.Text.Json.Nodes;

namespace Atlas.Server.Features.Objects.Contracts;

/// <summary>A page of the object library, newest change first; <see cref="Total"/> counts only what the reader may read.
/// <see cref="CanCreate"/>: for a listing of one type in one scope, whether the reader may add an object of it there (in the wiki
/// it then also changes the type's wiki objects).</summary>
public sealed record ObjectList(IReadOnlyList<ObjectSummaryDto> Items, int Total, int Page, int PageSize, bool CanCreate = false);

/// <summary>A listed object with its data, so a library shows its rows without reading each object.</summary>
public sealed record ObjectSummaryDto(Guid Id, string TypeId, string Scope, Guid? SpaceId, string Title, long Version, DateTime UpdatedAt, JsonObject? Data);

/// <summary>A page of the object trash, newest first; <see cref="Total"/> counts only what the reader may restore.</summary>
public sealed record ObjectTrashList(IReadOnlyList<TrashedObjectDto> Items, int Total, int Page, int PageSize);

/// <summary>A trashed object with who trashed it and when (no one for an object trashed before that was recorded). Its data comes
/// without the long text (<c>text</c>), of which only the plain <see cref="Excerpt"/> is sent (PERF3-1).</summary>
public sealed record TrashedObjectDto(Guid Id, string TypeId, string Scope, Guid? SpaceId, string Title, long Version, DateTime TrashedAt, Guid? TrashedById,
    string? TrashedByName, JsonObject? Data, string? Excerpt = null);
