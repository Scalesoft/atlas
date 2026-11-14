using System.Text.Json.Nodes;

namespace Atlas.Server.Features.Objects.Contracts;

/// <summary>POST /api/objects: a new object of a type the platform stores, in its scope (spaceId only for <c>space</c>).</summary>
public sealed record CreateObjectRequest(string? TypeId, string? Scope, Guid? SpaceId, JsonObject? Data);

/// <summary>POST /api/objects/{id}/changes: one change, applied once per <see cref="ChangeId"/>; with
/// <see cref="ExpectedVersion"/> only onto that version.</summary>
public sealed record ObjectChangeRequest(Guid? ChangeId, long? ExpectedVersion, JsonObject? Payload);

/// <summary>The object's version after the change; <see cref="Duplicate"/> when the change id had been applied before.</summary>
public sealed record ObjectChangeResult(long Version, bool Duplicate);
