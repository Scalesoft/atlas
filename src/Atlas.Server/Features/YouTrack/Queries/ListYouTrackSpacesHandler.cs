using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.YouTrack.Contracts;
using Atlas.YouTrack;

namespace Atlas.Server.Features.YouTrack.Queries;

public sealed class ListYouTrackSpacesHandler
{
    private readonly YouTrackActor m_actor;
    private readonly YouTrackSpaceAccess m_access;
    private readonly YouTrackConfigurationReader m_configuration;

    public ListYouTrackSpacesHandler(YouTrackActor actor, YouTrackSpaceAccess access, YouTrackConfigurationReader configuration)
    {
        m_actor = actor;
        m_access = access;
        m_configuration = configuration;
    }

    public async Task<YouTrackReaderSetup> Handle(ListYouTrackSpaces query, CancellationToken ct)
    {
        var actor = await m_actor.RequireAsync(false, ct);
        // The token stays protected: one the key ring cannot read is still a set-up integration, whose search answers 503.
        var stored = await m_configuration.ReadStoredAsync(ct);
        if (!stored.Enabled || stored.ProtectedToken.Length == 0 || !YouTrackClient.IsValidBaseUrl(stored.BaseUrl))
        {
            return new YouTrackReaderSetup([], null);
        }
        var spaces = new List<Guid>();
        foreach (var (spaceId, projects) in stored.SpaceProjects)
        {
            if (projects.Length == 0)
            {
                continue;
            }
            try
            {
                await m_access.RequireAsync(actor, spaceId, SpaceRole.Reader, ct);
                spaces.Add(spaceId);
            }
            catch (ApiException exception) when (exception.Status == 404)
            {
                // A space the actor does not read is not named.
            }
        }
        return new YouTrackReaderSetup(spaces, spaces.Count == 0 ? null : stored.BaseUrl.TrimEnd('/'));
    }
}
