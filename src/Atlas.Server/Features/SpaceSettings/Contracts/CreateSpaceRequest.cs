namespace Atlas.Server.Features.SpaceSettings.Contracts;

/// <param name="Description">Optional, as in SaveSpaceSettingsRequest.</param>
/// <param name="PinPolicy">None (when omitted), Default or Required (SpacePinPolicy).</param>
public sealed record CreateSpaceRequest(string Name, string? Icon, string? Color, IReadOnlyList<CreateSpaceGroupGrant?>? Groups, string? Description = null, string? PinPolicy = null);
