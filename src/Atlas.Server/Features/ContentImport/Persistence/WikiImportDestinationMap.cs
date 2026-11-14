using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.ContentImport.Domain;

namespace Atlas.Server.Features.ContentImport.Persistence;

public sealed class WikiImportDestinationMap : ClassMapping<WikiImportDestination>
{
    public WikiImportDestinationMap()
    {
        Table("`WikiImportDestination`");
        Id(x => x.Id, m =>
        {
            m.Generator(Generators.Assigned);
            m.Column("`Id`");
        });
        Property(x => x.SourceId);
        Property(x => x.SpaceId);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
