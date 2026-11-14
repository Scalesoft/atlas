using System.Text.Json.Nodes;

namespace Atlas.Server.Features.Objects.Commands;

public sealed record CreateObject(string? TypeId, string? Scope, Guid? SpaceId, JsonObject? Data);
