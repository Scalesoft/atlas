using Atlas.Server.Features.Ai.Settings.Contracts;

namespace Atlas.Server.Features.Ai.Settings.SavedProfiles;

public sealed record SavedProfileView(Guid Id, string Name, string Version, AiProfileView Profile, bool IsActive);
public sealed record SavedProfileList(IReadOnlyList<SavedProfileView> Items, string? ActiveVersion);
public sealed record SaveSavedProfileRequest(string Name, SaveAiProfileRequest Profile, string? ExpectedVersion)
{
    public override string ToString() => "SaveSavedProfileRequest { Profile = [redacted] }";
}
public sealed record ActivateSavedProfileRequest(string? ExpectedVersion, string? ExpectedSavedVersion);
public sealed record SnapshotCurrentProfileRequest(string Name, string? ExpectedVersion);
public sealed record SavedProfileVersionRequest(string? ExpectedVersion);
public sealed record SavedModelDiscoveryRequest(string Provider, string Location, string? BaseUrl, string? ApiKey,
    bool ClearApiKey, string? ExpectedSavedVersion)
{
    public override string ToString() => "SavedModelDiscoveryRequest { ApiKey = [redacted] }";
}
public sealed record ListSavedProfiles(string Kind);
public sealed record GetSavedProfile(string Kind, Guid Id);
public sealed record SaveSavedProfile(string Kind, Guid? Id, SaveSavedProfileRequest Request);
public sealed record DeleteSavedProfile(string Kind, Guid Id, string? ExpectedVersion);
public sealed record ActivateSavedProfile(string Kind, Guid Id, ActivateSavedProfileRequest Request);
public sealed record SnapshotCurrentProfile(string Kind, SnapshotCurrentProfileRequest Request);
public sealed record PrepareSavedModelDiscovery(string Kind, Guid? Id, SavedModelDiscoveryRequest Request);
public sealed record BeginSavedProfileTest(string Kind, Guid Id, string? ExpectedVersion);
public sealed record CompleteSavedProfileTest(string Kind, Guid Id, Guid RevisionId);
