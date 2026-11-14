using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Hub.Domain;

namespace Atlas.Server.Features.Hub.Persistence;

public sealed class HubConnectionMap : ClassMapping<HubConnection>
{
    public HubConnectionMap()
    {
        Table("`HubConnection`");
        Id(x => x.Id, m => { m.Column("`Id`"); m.Generator(Generators.Assigned); });

    }
}

