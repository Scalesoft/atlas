namespace Atlas.Server.Features.YouTrack.Contracts;

public sealed record YouTrackSettingsDto(bool ModuleEnabled, bool Enabled, string BaseUrl, bool HasToken, string? Version,
    YouTrackSpace[] Spaces, YouTrackSpaceProjects[] SpaceProjects, string Status);
