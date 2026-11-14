using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.ContentImport.Domain;

namespace Atlas.Server.Features.ContentImport.Persistence;

public sealed class WikiImportSourceMap : ClassMapping<WikiImportSource>
{
    public WikiImportSourceMap()
    {
        Table("`WikiImportSource`");
        Id(x => x.Id, m =>
        {
            m.Generator(Generators.Assigned);
            m.Column("`Id`");
        });
        Property(x => x.SourceKey);
    }
}
