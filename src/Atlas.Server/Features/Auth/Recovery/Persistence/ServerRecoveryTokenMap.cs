using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Auth.Recovery.Domain;

namespace Atlas.Server.Features.Auth.Recovery.Persistence;

public sealed class ServerRecoveryTokenMap : ClassMapping<ServerRecoveryToken>
{
    public ServerRecoveryTokenMap()
    {
        Table("`ServerRecoveryToken`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.UserId);
        Property(x => x.TokenHash, m => m.Type(NHibernateUtil.BinaryBlob));
        Property(x => x.IssuedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ExpiresAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ConsumedAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
