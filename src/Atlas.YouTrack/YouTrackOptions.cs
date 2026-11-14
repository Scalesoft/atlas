namespace Atlas.YouTrack;

public sealed class YouTrackOptions
{
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "";

    public string Token { get; set; } = "";

    public Dictionary<Guid, string[]> SpaceProjects { get; set; } = [];

    /// <summary>The token is a credential; it must never be spelled out however the host reports these options.</summary>
    public override string ToString()
    {
        return $"YouTrackOptions {{ Enabled = {Enabled}, Token = [redacted] }}";
    }
}
