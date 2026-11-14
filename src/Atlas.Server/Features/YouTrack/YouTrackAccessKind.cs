using Atlas.Server.Features.Access.Contracts;
using Atlas.Server.Features.Access.Domain;
using Atlas.YouTrack;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.YouTrack;

/// <summary>
/// The reference plugin kind (docs/access-model.md §5): read-only and derived. The YouTrack projects allowed for a space are readable
/// by everyone with at least Reader in that space, exactly what the YouTrack search and resolve reads check (YouTrackReadRunner,
/// YouTrackSpaceAccess: the space role, WikiAdmin as Admin; page-only access to a space does not count). Offered only while the
/// integration is configured.
/// </summary>
public sealed class YouTrackAccessKind : IAccessKind
{
    private static readonly AccessKindDescriptor Kind = new("youtrack", "YouTrack projekt", "YouTrack", "blocks", "space", "YouTrack",
    [
        new(AccessLevels.Read, "Vidí úkoly projektu ve wiki")
    ]);

    private readonly ISession m_session;
    private readonly SpaceRoles m_roles;
    private readonly YouTrackConfigurationReader m_configuration;

    public YouTrackAccessKind(ISession session, SpaceRoles roles, YouTrackConfigurationReader configuration)
    {
        m_session = session;
        m_roles = roles;
        m_configuration = configuration;
    }

    public AccessKindDescriptor Descriptor => Kind;

    public IAccessGrantWriter? Writer => null;

    public async Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        return await OptionsAsync(ct) is not null;
    }

    public async Task<IReadOnlyList<AccessEntry>> ForPrincipalAsync(AccessPrincipal who, AccessScope scope, CancellationToken ct)
    {
        if (await OptionsAsync(ct) is not { } options)
        {
            return [];
        }
        var projects = options.SpaceProjects.Where(p => p.Value.Length > 0 && scope.Includes(p.Key)).ToArray();
        if (projects.Length == 0)
        {
            return [];
        }
        var wikiAdmin = who.Account is { } account && await EffectiveWikiAdmin.IsAdminAsync(m_session, account, ct);
        var roles = wikiAdmin ? null : await AccessGrantRows.SpaceRolesAsync(m_session, m_roles, who, ct);
        var spaces = await AccessGrantRows.SpacesAsync(m_session, projects.Select(p => p.Key), ct);
        return projects.Where(p => spaces.ContainsKey(p.Key) && (wikiAdmin || roles!.ContainsKey(p.Key))).Select(p =>
        {
            var space = spaces[p.Key];
            var source = wikiAdmin
                ? new AccessEntrySource(AccessSources.Automatic, AccessSources.WikiAdminLabel, AccessLevels.Read)
                : new AccessEntrySource(AccessSources.Derived, AccessSources.SpaceRoleLabel, AccessLevels.Read);
            return new AccessEntry(Kind.Id, space.Id.ToString(), string.Join(", ", p.Value), null, space.Id, space.Name, AccessLevels.Read, [source],
                AccessNotes.Count(p.Value.Length, "projekt", "projekty", "projektů"));
        }).OrderBy(e => e.SpaceName, StringComparer.CurrentCulture).ToArray();
    }

    public Task<ResourceAccess?> ForResourceAsync(Guid resourceId, AccessView view, string? query, CancellationToken ct) => Task.FromResult<ResourceAccess?>(null);

    public Task<IReadOnlyList<AccessResource>> SearchAsync(string? query, CancellationToken ct) => Task.FromResult<IReadOnlyList<AccessResource>>([]);

    /// <summary>The configured integration, or null when it is off, incomplete or its stored token cannot be read.</summary>
    private async Task<YouTrackOptions?> OptionsAsync(CancellationToken ct)
    {
        try
        {
            var options = (await m_configuration.ReadConfigurationAsync(ct)).Options;
            return YouTrackClient.IsConfigured(options) ? options : null;
        }
        catch (YouTrackUnavailableException)
        {
            return null;
        }
    }
}
