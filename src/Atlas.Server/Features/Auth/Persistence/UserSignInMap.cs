using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Auth.Domain;

namespace Atlas.Server.Features.Auth.Persistence;

public sealed class UserSignInMap : ClassMapping<UserSignIn>
{
    public UserSignInMap()
    {
        Table("`UserSignIn`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.UserId);
        Property(x => x.AuthenticatedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.AuthenticationMethod);
    }
}
