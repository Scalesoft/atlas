namespace Atlas.Server.Features.YouTrack.Domain;

public class YouTrackConfiguration
{
    public virtual Guid Id { get; set; }

    public virtual bool Enabled { get; set; }

    public virtual string BaseUrl { get; set; } = "";

    public virtual string ProtectedToken { get; set; } = "";

    public virtual string SpaceProjectsJson { get; set; } = "{}";

    /// <summary>The projects' short names by stable ID as YouTrack last answered them (StoreYouTrackProjectKeys), so that the place
    /// lexicon recognises issue keys without asking YouTrack.</summary>
    public virtual string ProjectKeysJson { get; set; } = "{}";

    public virtual Guid ChangedById { get; set; }

    public virtual DateTime ActiveFrom { get; set; }

    public virtual DateTime? ActiveTo { get; set; }
}
