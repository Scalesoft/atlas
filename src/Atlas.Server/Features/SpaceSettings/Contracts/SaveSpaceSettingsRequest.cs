namespace Atlas.Server.Features.SpaceSettings.Contracts;

/// <param name="PinPolicy">None, Default or Required (SpacePinPolicy).</param>
/// <param name="CommentsEnabled">„Komentáře“: whether readers may post comments; null keeps the current setting.</param>
/// <param name="PublicationMode">3.6-I „Publikace“ its pages inherit: Manual, Automatic or Live; null keeps the current setting.</param>
public sealed record SaveSpaceSettingsRequest(string Name, string Icon, string Color, string? Description, string PinPolicy, DateTime ExpectedVersion,
    bool? CommentsEnabled = null, string? PublicationMode = null);
