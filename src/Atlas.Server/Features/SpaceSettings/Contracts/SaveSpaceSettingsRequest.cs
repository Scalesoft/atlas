namespace Atlas.Server.Features.SpaceSettings.Contracts;

/// <param name="PinPolicy">None, Default or Required (SpacePinPolicy).</param>
/// <param name="CommentsEnabled">„Komentáře“: whether readers may post comments; null keeps the current setting.</param>
/// <param name="PublicationMode">3.6-I „Publikace“ its pages inherit: Manual, Automatic or Live; null keeps the current setting.</param>
/// <param name="VerificationIntervalMonths">5.2 „Ověřování obsahu“: 3, 6 or 12 months, 0 switches it off; null keeps the current setting.</param>
public sealed record SaveSpaceSettingsRequest(string Name, string Icon, string Color, string? Description, string PinPolicy, DateTime ExpectedVersion,
    bool? CommentsEnabled = null, string? PublicationMode = null, int? VerificationIntervalMonths = null);
