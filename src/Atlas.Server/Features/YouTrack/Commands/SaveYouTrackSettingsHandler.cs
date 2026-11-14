using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Atlas.Server.Features.YouTrack.Contracts;
using Atlas.Server.Features.YouTrack.Domain;
using Atlas.Server.Features.YouTrack.Queries;
using Atlas.YouTrack;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.YouTrack.Commands;

public sealed class SaveYouTrackSettingsHandler
{
    public const string TokenPurpose = "ScaleAtlas.YouTrack.Token.v1";
    private readonly ISession m_session;
    private readonly IAtlasLocks m_locks;
    private readonly YouTrackActor m_actor;
    private readonly IOptions<YouTrackOptions> m_defaults;
    private readonly IDataProtector m_protector;

    public SaveYouTrackSettingsHandler(ISession session, IAtlasLocks locks, YouTrackActor actor,
        IOptions<YouTrackOptions> defaults, IDataProtectionProvider protection)
    {
        m_session = session;
        m_locks = locks;
        m_actor = actor;
        m_defaults = defaults;
        m_protector = protection.CreateProtector(TokenPurpose);
    }

    public async Task<YouTrackSettingsDto> Handle(SaveYouTrackSettings command, CancellationToken ct)
    {
        var request = command.Request;
        // Authorize before validating the request body: legacy resolved the actor as the endpoint argument
        // (CurrentUser.GetAsync) before SaveAsync ever looked at the payload, so a stale stamp or a non-admin
        // actor answers 401/403 even against a malformed body. The advisory lock keeps serializing with
        // user/group administration in the same position as before.
        await m_locks.AcquireContentExclusiveAsync(ct);
        var actor = await m_actor.RequireAsync(true, ct);
        var baseUrl = (request.BaseUrl ?? "").Trim().TrimEnd('/');
        if (baseUrl.Length > 2048 || baseUrl.Length > 0 && !YouTrackClient.IsValidBaseUrl(baseUrl))
        {
            throw new ApiException(400, "Adresa YouTrack musí být HTTPS bez přihlašovacích údajů, parametrů a fragmentu.");
        }
        if (request.Token is { Length: > 8192 } || request.Token?.Any(char.IsControl) == true)
        {
            throw new ApiException(400, "Přístupový token nemá platný formát.");
        }
        if (request.ClearToken && !string.IsNullOrWhiteSpace(request.Token))
        {
            throw new ApiException(400, "Token nelze současně změnit a odstranit.");
        }
        if (request.SpaceProjects is null || request.SpaceProjects.Length > 500
            || request.SpaceProjects.Select(x => x.SpaceId).Distinct().Count() != request.SpaceProjects.Length)
        {
            throw new ApiException(400, "Každý prostor může mít právě jednu sadu povolených projektů.");
        }
        var mapping = new Dictionary<Guid, string[]>();
        foreach (var item in request.SpaceProjects)
        {
            if (item.ProjectIds is null || item.ProjectIds.Length > 100
                || item.ProjectIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            {
                throw new ApiException(400, "Použijte stabilní ID projektů YouTrack, například 0-7; nejvýše 100 na prostor.");
            }
            if (item.ProjectIds.Length > 0)
            {
                mapping[item.SpaceId] = item.ProjectIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            }
        }
        var previous = await CurrentYouTrackSettings.LoadAsync(m_session, ct);
        AdministrationVersion.Expect(previous?.ActiveFrom, request.ExpectedVersion);
        foreach (var spaceId in mapping.Keys)
        {
            if (!await LiveSpace.ExistsAsync(m_session, spaceId, ct))
            {
                throw new ApiException(400, "Některý prostor již neexistuje.");
            }
        }
        var defaults = m_defaults.Value;
        var encrypted = previous?.ProtectedToken ?? (string.IsNullOrWhiteSpace(defaults.Token) ? "" : m_protector.Protect(defaults.Token));
        if (request.ClearToken)
        {
            encrypted = "";
        }
        else if (!string.IsNullOrWhiteSpace(request.Token))
        {
            encrypted = m_protector.Protect(request.Token.Trim());
        }
        // Changing origin must never silently send the old credential to a new server.
        var oldUrl = (previous?.BaseUrl ?? defaults.BaseUrl).TrimEnd('/');
        if (!string.Equals(baseUrl, oldUrl, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(request.Token))
        {
            encrypted = "";
        }
        // Another server's projects have other short names.
        var projectKeys = previous is not null && string.Equals(baseUrl, previous.BaseUrl, StringComparison.Ordinal) ? previous.ProjectKeysJson : "{}";
        if (request.Enabled && (baseUrl.Length == 0 || encrypted.Length == 0))
        {
            throw new ApiException(400, "Pro zapnutí integrace vyplňte adresu a nový přístupový token.");
        }
        var now = Temporal.NextBoundary(previous?.ActiveFrom ?? DateTime.UnixEpoch, DateTime.UtcNow);
        if (previous is not null)
        {
            previous.ActiveTo = now;
            await m_session.FlushAsync(ct);
        }
        await m_session.SaveAsync(new YouTrackConfiguration
        {
            Id = Guid.NewGuid(), Enabled = request.Enabled, BaseUrl = baseUrl, ProtectedToken = encrypted,
            SpaceProjectsJson = JsonSerializer.Serialize(mapping), ProjectKeysJson = projectKeys, ChangedById = actor.Id, ActiveFrom = now
        }, ct);
        return await CurrentYouTrackSettings.ReadAsync(m_session, defaults, ct);
    }
}
