namespace Atlas.Server.Features.Access.Contracts;

/// <summary>One path to a level. Via: direct (a grant of the person or group itself), group (a grant of a group the person is in),
/// automatic (WikiAdmin, the space Admin role) or derived (follows from another kind, for example the space role). Version is the CAS
/// token of the grant (its ActiveFrom) for changing or revoking it. Effective is false for a current grant outside its validity.</summary>
public sealed record AccessEntrySource(string Via, string Label, string Level, Guid? GroupId = null, Guid? GrantId = null,
    string? Version = null, DateTime? StartAt = null, DateTime? ExpiresAt = null, bool Effective = true, string? GroupSource = null,
    bool VerificationLimited = false, DateTime? GrantExpiresAt = null);
