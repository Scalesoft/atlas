using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.ContentImport.Domain;

namespace Atlas.Server.Features.ContentImport.Persistence;

public sealed class WikiImportBatchMap : ClassMapping<WikiImportBatch>
{
    public WikiImportBatchMap()
    {
        Table("`WikiImportBatch`");
        Id(x => x.Id, m =>
        {
            m.Generator(Generators.Assigned);
            m.Column("`Id`");
        });
        Property(x => x.SourceId);
        Property(x => x.CreatedBy);
        Property(x => x.CreatedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ExpiresAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.CompletedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ArchiveSha256);
        Property(x => x.OptionsJson, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.ReportJson, m => m.Type(NHibernateUtil.StringClob));
    }
}
