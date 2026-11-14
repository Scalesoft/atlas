using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.PageOrganization.Domain;

namespace Atlas.Server.Features.PageOrganization.Persistence;

public sealed class SpaceOrganizationMap : ClassMapping<SpaceOrganization>
{
    public SpaceOrganizationMap()
    {
        Table("`SpaceOrganization`");
        Mutable(false);
        Id(x => x.SpaceId, m =>
        {
            m.Generator(Generators.Assigned);
            m.Column("`SpaceId`");
        });
        Property(x => x.Version);
        Property(x => x.SortRootByTitle);
    }
}
