using Atlas.Server.Features.Auth.Contracts;
using Atlas.Server.Features.Auth.OpenIdConnect.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.OpenIdConnect.Queries;

public sealed class ListIdentitiesHandler
{
    private readonly ISession m_session;
    private readonly ICurrentActor m_actor;
    private readonly AtlasOidcOptions m_settings;

    public ListIdentitiesHandler(ISession session, ICurrentActor actor, AtlasOidcOptions settings)
    {
        m_session = session;
        m_actor = actor;
        m_settings = settings;
    }

    public async Task<LinkedIdentitiesDto> Handle(ListIdentities query, CancellationToken ct)
    {
        var user = await m_actor.RequireAsync(ct);
        var identities = await m_session.QueryOver<ExternalIdentity>()
            .Where(x => x.UserId == user.Id && x.ActiveTo == null)
            .ListAsync(ct);
        var items = (from provider in m_settings.Providers
            join identity in identities on provider.Issuer equals identity.Issuer
            select new LinkedIdentityDto(provider.Id, provider.DisplayName, identity.ActiveFrom)).ToList();
        return new LinkedIdentitiesDto(items);
    }
}
