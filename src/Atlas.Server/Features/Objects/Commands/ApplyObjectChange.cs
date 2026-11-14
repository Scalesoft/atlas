using System.Text.Json.Nodes;

namespace Atlas.Server.Features.Objects.Commands;

public sealed record ApplyObjectChange(Guid ObjectId, Guid? ChangeId, long? ExpectedVersion, JsonObject? Payload);
