using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Hub.Domain;

namespace Atlas.Server.Features.Hub.Persistence;

public sealed class HubConnectionRevisionMap : ClassMapping<HubConnectionRevision>
{
    public HubConnectionRevisionMap()
    {
        Table("`HubConnectionRevision`");
        // Closing an interval writes "ActiveTo" alone: the runtime database role may update nothing else (RuntimeDatabaseRole).
        DynamicUpdate(true);
        Id(x => x.Id, m => { m.Column("`Id`"); m.Generator(Generators.Assigned); });
        Property(x => x.ConnectionId);
        Property(x => x.Issuer, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.BaseUrl, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.ProtectedToken, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.Mode);
        Property(x => x.ChangedById);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}

