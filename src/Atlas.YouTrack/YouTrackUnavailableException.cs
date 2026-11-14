namespace Atlas.YouTrack;

public sealed class YouTrackUnavailableException : Exception
{
    public YouTrackUnavailableException()
        : base("YouTrack is currently unavailable.")
    {
    }
}
