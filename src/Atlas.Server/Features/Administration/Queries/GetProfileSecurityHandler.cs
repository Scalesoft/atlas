using Atlas.Server.Features.Administration.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Queries;

public sealed class GetProfileSecurityHandler
{
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;

    public GetProfileSecurityHandler(ISession session, AdministrationAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<ProfileSecurity> Handle(GetProfileSecurity query, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(ct);
        var user = await AdministrationReads.UserAsync(m_session, actor.Id, ct);
        // Username is null while the account has only the placeholder of an external sign-in; Email suggests one.
        return new ProfileSecurity(user.HasBuiltinCredential, user.CredentialState,
            UserAccount.HasPlaceholderUsername(actor.Username) ? null : actor.Username, actor.Email);
    }
}
