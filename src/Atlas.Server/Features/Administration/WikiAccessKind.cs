using Atlas.Server.Features.Access.Contracts;
using Atlas.Server.Features.Access.Domain;

namespace Atlas.Server.Features.Administration;

/// <summary>The global WikiAdmin role (UserAccount.IsWikiAdmin) as an access kind with the one level manage. Read-only here: the role
/// is switched in the account form (SaveUser, with a fresh sign-in). The source is automatic when the OIDC admin mapping granted it
/// (OidcAdminIssuer), direct otherwise.</summary>
public sealed class WikiAccessKind(NHibernate.ISession session) : IAccessKind
{
    private static readonly AccessKindDescriptor Kind = new("wiki", "Wiki", "Wiki", "shield-check", null, null,
    [
        new(AccessLevels.Manage, "Správce wiki: všechno ve všech prostorech")
    ]);

    public AccessKindDescriptor Descriptor => Kind;

    public IAccessGrantWriter? Writer => null;

    public async Task<IReadOnlyList<AccessEntry>> ForPrincipalAsync(AccessPrincipal who, AccessScope scope, CancellationToken ct)
    {
        if (scope.SpaceId is not null || who.Account is not { } account)
        {
            return [];
        }
        var authority = await EffectiveWikiAdmin.ReadAsync(session, account, ct);
        var sources = new List<AccessEntrySource>();
        if (account.IsWikiAdmin)
            sources.Add(account.OidcAdminIssuer is null
                ? new AccessEntrySource(AccessSources.Direct, AccessSources.WikiAdminLabel, AccessLevels.Manage, Effective: account.IsActive)
                : new AccessEntrySource(AccessSources.Automatic, "Z mapování poskytovatele přihlášení", AccessLevels.Manage, Effective: account.IsActive));
        if (authority.HubVerifiedUntil is { } until)
            sources.Add(new AccessEntrySource(AccessSources.Automatic, "Ze skupiny Hubu", AccessLevels.Manage, ExpiresAt: until, Effective: true, VerificationLimited: true));
        if (sources.Count == 0) return [];
        IReadOnlyList<AccessEntry> entries =
        [
            new AccessEntry(Kind.Id, "wiki", "Celá wiki", null, null, null, authority.Authority(DateTime.UtcNow).Allowed ? AccessLevels.Manage : null, sources)
        ];
        return entries;
    }

    public Task<ResourceAccess?> ForResourceAsync(Guid resourceId, AccessView view, string? query, CancellationToken ct) => Task.FromResult<ResourceAccess?>(null);

    public Task<IReadOnlyList<AccessResource>> SearchAsync(string? query, CancellationToken ct) => Task.FromResult<IReadOnlyList<AccessResource>>([]);
}
