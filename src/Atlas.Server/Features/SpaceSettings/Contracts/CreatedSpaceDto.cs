using Atlas.Server.Features.SpaceCovers.Contracts;

namespace Atlas.Server.Features.SpaceSettings.Contracts;

/// <param name="Cover">The automatic image of the new space, so its card shows it at once.</param>
/// <param name="PinPolicy">None, Default or Required (SpacePinPolicy), so the sidebar shows the rule at once.</param>
public sealed record CreatedSpaceDto(Guid Id, string Name, string Icon, string Color, string Role, SpaceCoverDto Cover, string Description, string PinPolicy);
