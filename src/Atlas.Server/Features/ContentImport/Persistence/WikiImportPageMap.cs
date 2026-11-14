using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.ContentImport.Domain;

namespace Atlas.Server.Features.ContentImport.Persistence;

public sealed class WikiImportPageMap : ClassMapping<WikiImportPage>
{
    public WikiImportPageMap()
    {
        Table("`WikiImportPage`");
        Id(x => x.Id, m =>
        {
            m.Generator(Generators.Assigned);
            m.Column("`Id`");
        });
        Property(x => x.SourceId);
        Property(x => x.SourcePath);
        Property(x => x.PageId);
    }
}
