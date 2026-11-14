using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Auth.Recovery.Domain;

namespace Atlas.Server.Features.Auth.Recovery.Persistence;

public sealed class BuiltinAccountTokenMap : ClassMapping<BuiltinAccountToken>
{
    public BuiltinAccountTokenMap()
    {
        Table("`BuiltinAccountToken`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.UserId);
        Property(x => x.TokenHash, m => m.Type(NHibernateUtil.BinaryBlob));
        Property(x => x.Purpose);
        Property(x => x.ExpiresAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ConsumedAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
