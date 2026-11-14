using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Atlas.Server.Features.YouTrack.Commands;
using Atlas.Server.Features.YouTrack.Domain;
using Atlas.Server.Features.YouTrack.Queries;
using Atlas.YouTrack;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.YouTrack;

public sealed class YouTrackConfigurationReader
{
    private readonly ISession m_session;
    private readonly YouTrackActor m_actor;
    private readonly YouTrackSpaceAccess m_access;
    private readonly IOptions<YouTrackOptions> m_defaults;
    private readonly IDataProtector m_protector;

    public YouTrackConfigurationReader(ISession session, YouTrackActor actor, YouTrackSpaceAccess access,
        IOptions<YouTrackOptions> defaults, IDataProtectionProvider protection)
    {
        m_session = session;
        m_actor = actor;
        m_access = access;
        m_defaults = defaults;
        m_protector = protection.CreateProtector(SaveYouTrackSettingsHandler.TokenPurpose);
    }

    /// <summary>The effective configuration after the actor check, as a message result: the token stays data-protected and nothing is
    /// unprotected here. A token that comes only from the server configuration is protected here, for this one purpose.</summary>
    public async Task<YouTrackProtectedConfiguration> ReadProtectedAsync(Guid? spaceId, CancellationToken ct)
    {
        await AuthorizeAsync(spaceId, ct);
        return await ReadStoredAsync(ct);
    }

    /// <summary><see cref="ReadProtectedAsync"/> without the actor check, for callers that authorize themselves.</summary>
    public async Task<YouTrackProtectedConfiguration> ReadStoredAsync(CancellationToken ct)
    {
        var row = await CurrentYouTrackSettings.LoadAsync(m_session, ct);
        if (row is null)
        {
            var fallback = m_defaults.Value;
            return new YouTrackProtectedConfiguration(FallbackRevision(fallback), fallback.Enabled, fallback.BaseUrl,
                string.IsNullOrEmpty(fallback.Token) ? "" : m_protector.Protect(fallback.Token), fallback.SpaceProjects);
        }
        return Stored(row);
    }

    /// <summary>The effective configuration without an actor check, for callers that authorize themselves (the YouTrack access kind
    /// only reads which projects each space allows). Its token must never leave the server.</summary>
    public async Task<YouTrackConfigurationSnapshot> ReadConfigurationAsync(CancellationToken ct)
    {
        var row = await CurrentYouTrackSettings.LoadAsync(m_session, ct);
        if (row is null)
        {
            var fallback = m_defaults.Value;
            return new YouTrackConfigurationSnapshot(FallbackRevision(fallback), fallback);
        }
        return Stored(row).Unprotect(m_protector);
    }

    /// <summary>The stored short names of the projects the space allows while the integration is on, without asking YouTrack and
    /// without an actor check (the place lexicon authorizes the space). Empty until YouTrack named them (StoreYouTrackProjectKeys).</summary>
    public async Task<string[]> ProjectKeysAsync(Guid spaceId, CancellationToken ct)
    {
        var row = await CurrentYouTrackSettings.LoadAsync(m_session, ct);
        if (row is null || !m_defaults.Value.Enabled || !row.Enabled
            || !JsonSerializer.Deserialize<Dictionary<Guid, string[]>>(row.SpaceProjectsJson)!.TryGetValue(spaceId, out var projects))
        {
            return [];
        }
        var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(row.ProjectKeysJson)!;
        return projects.Select(id => keys.GetValueOrDefault(id)).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public async Task EnsureUnchangedAsync(Guid? spaceId, string revision, CancellationToken ct)
    {
        await AuthorizeAsync(spaceId, ct);
        if ((await ReadStoredAsync(ct)).Revision != revision)
        {
            throw new ApiException(409, "Nastavení YouTrack se mezitím změnilo. Zkuste požadavek znovu.");
        }
    }

    private async Task AuthorizeAsync(Guid? spaceId, CancellationToken ct)
    {
        var actor = await m_actor.RequireAsync(spaceId is null, ct);
        if (spaceId is { } id)
        {
            await m_access.RequireAsync(actor, id, SpaceRole.Reader, ct);
        }
    }

    private static string FallbackRevision(YouTrackOptions fallback)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fallback))));
    }

    private YouTrackProtectedConfiguration Stored(YouTrackConfiguration row)
    {
        return new YouTrackProtectedConfiguration(row.Id.ToString("N"), m_defaults.Value.Enabled && row.Enabled, row.BaseUrl,
            row.ProtectedToken, JsonSerializer.Deserialize<Dictionary<Guid, string[]>>(row.SpaceProjectsJson)!);
    }
}
