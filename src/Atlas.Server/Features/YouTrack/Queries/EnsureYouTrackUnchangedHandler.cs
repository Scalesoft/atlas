namespace Atlas.Server.Features.YouTrack.Queries;

public sealed class EnsureYouTrackUnchangedHandler
{
    private readonly YouTrackConfigurationReader m_configuration;

    public EnsureYouTrackUnchangedHandler(YouTrackConfigurationReader configuration)
    {
        m_configuration = configuration;
    }

    public Task Handle(EnsureYouTrackUnchanged query, CancellationToken ct)
    {
        return m_configuration.EnsureUnchangedAsync(query.SpaceId, query.Revision, ct);
    }
}
