using Atlas.Server.Features.Spaces.Domain;
using System.Text.Json;
using Atlas.Server.Features.YouTrack.Contracts;
using Atlas.Server.Features.YouTrack.Domain;
using Atlas.YouTrack;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.YouTrack.Queries;

internal static class CurrentYouTrackSettings
{
    public static Task<YouTrackConfiguration?> LoadAsync(ISession session, CancellationToken ct)
    {
        return session.QueryOver<YouTrackConfiguration>().Where(x => x.ActiveTo == null).SingleOrDefaultAsync(ct)!;
    }

    public static async Task<YouTrackSettingsDto> ReadAsync(ISession session, YouTrackOptions source, CancellationToken ct)
    {
        var row = await LoadAsync(session, ct);
        var baseUrl = row?.BaseUrl ?? source.BaseUrl;
        var hasToken = row is null ? !string.IsNullOrWhiteSpace(source.Token) : row.ProtectedToken.Length > 0;
        var enabled = row?.Enabled ?? YouTrackClient.IsConfigured(source);
        var mapping = row is null ? source.SpaceProjects : JsonSerializer.Deserialize<Dictionary<Guid, string[]>>(row.SpaceProjectsJson)!;
        var spaces = await LiveSpace.Query(session).OrderBy(x => x.Name).Asc.ListAsync(ct);
        var status = !source.Enabled || !enabled ? "disabled"
            : hasToken && YouTrackClient.IsValidBaseUrl(baseUrl) ? "configured" : "not-configured";
        return new YouTrackSettingsDto(source.Enabled, enabled, baseUrl, hasToken,
            row is null ? null : AdministrationVersion.Format(row.ActiveFrom),
            spaces.Select(x => new YouTrackSpace(x.Id, x.Name)).ToArray(),
            mapping.Select(x => new YouTrackSpaceProjects(x.Key, x.Value)).ToArray(), status);
    }
}
