using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.ContentImport.Domain;

namespace Atlas.Server.Features.ContentImport.Persistence;

public sealed class WikiImportCheckpointMap : ClassMapping<WikiImportCheckpoint>
{
    public WikiImportCheckpointMap()
    {
        Table("`WikiImportCheckpoint`");
        Id(x => x.Id, m =>
        {
            m.Generator(Generators.Assigned);
            m.Column("`Id`");
        });
        Property(x => x.ImportPageId);
        Property(x => x.RevisionId);
        Property(x => x.SourceSha256);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
