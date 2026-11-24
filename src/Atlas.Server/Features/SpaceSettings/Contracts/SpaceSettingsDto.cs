using Atlas.Server.Features.SpaceCovers.Contracts;

namespace Atlas.Server.Features.SpaceSettings.Contracts;

/// <param name="PinPolicy">None, Default or Required (SpacePinPolicy).</param>
/// <param name="Cover">The image of the space, never null; saved on its own (/api/spaces/{id}/cover), not with the form, so not
/// part of Version.</param>
public sealed record SpaceSettingsDto(Guid Id, string Name, string Icon, string Color, string Description, string PinPolicy, DateTime Version,
    SpaceCoverDto Cover, bool CommentsEnabled, string PublicationMode, int? VerificationIntervalMonths = null);
