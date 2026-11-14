using Atlas.Server.Features.YouTrack.Domain;

namespace Atlas.Server.Features.YouTrack.Queries;

public sealed class ReadYouTrackConfigurationHandler
{
    private readonly YouTrackConfigurationReader m_configuration;

    public ReadYouTrackConfigurationHandler(YouTrackConfigurationReader configuration)
    {
        m_configuration = configuration;
    }

    public Task<YouTrackProtectedConfiguration> Handle(ReadYouTrackConfiguration query, CancellationToken ct)
    {
        return m_configuration.ReadProtectedAsync(query.SpaceId, ct);
    }
}
