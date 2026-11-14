using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Hub.Domain;

namespace Atlas.Server.Features.Hub.Persistence;

public sealed class HubGroupBindingMap : ClassMapping<HubGroupBinding>
{
    public HubGroupBindingMap()
    {
        Table("`HubGroupBinding`");
        Id(x => x.GroupId, m => { m.Column("`GroupId`"); m.Generator(Generators.Assigned); });
        Property(x => x.ConnectionId);
        Property(x => x.ExternalGroupId, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.Enabled);
        Property(x => x.GrantsWikiAdmin);
    }
}

