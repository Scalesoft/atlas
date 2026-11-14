namespace Atlas.Server.Features.YouTrack.Contracts;

public sealed record SaveYouTrackSettingsRequest(bool Enabled, string BaseUrl, string? Token, bool ClearToken,
    YouTrackSpaceProjects[] SpaceProjects, string? ExpectedVersion)
{
    public override string ToString() => "SaveYouTrackSettings { Token = [redacted] }";
}
