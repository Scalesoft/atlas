using Atlas.Server.Features.SpaceAi.Contracts;

namespace Atlas.Server.Features.SpaceAi.Commands;

public sealed record SaveSpaceAiSettings(Guid SpaceId, SaveSpaceAiSettingsRequest Request);
