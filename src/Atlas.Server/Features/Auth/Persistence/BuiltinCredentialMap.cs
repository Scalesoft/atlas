using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Auth.Domain;

namespace Atlas.Server.Features.Auth.Persistence;

public sealed class BuiltinCredentialMap : ClassMapping<BuiltinCredential>
{
    public BuiltinCredentialMap()
    {
        Table("`BuiltinCredential`");
        Id(x => x.UserId, m =>
        {
            m.Column("`UserId`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.PasswordHash);
    }
}
