using System.Text.Json.Nodes;

namespace Atlas.Server.Features.Objects.Contracts;

/// <summary>An object as the reader may see it: the envelope, its editing model (camelCase), whether the reader may change it,
/// its plain-text title and, for the types the platform stores, its data (null for pages and boards).</summary>
public sealed record ObjectDto(Guid Id, string TypeId, string Scope, Guid? SpaceId, Guid? OwnerId, long Version, DateTime CreatedAt,
    Guid? CreatedBy, DateTime UpdatedAt, string EditingModel, bool CanEdit, string Title, JsonObject? Data);
