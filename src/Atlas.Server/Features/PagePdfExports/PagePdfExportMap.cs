using FluentMigrator;
using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
namespace Atlas.Server.Features.PagePdfExports;

[Migration(2026100900)]
public sealed class PagePdfExportMigration() : EmbeddedSchemaMigration("2026100900_page_pdf_exports.sql");
[Migration(2026101300, "What an automatic PDF states on its cover")]
public sealed class PagePdfExportCoverDetailsMigration() : EmbeddedSchemaMigration("2026101300_page_pdf_export_cover_details.sql");
[Migration(2026101640, "Several automatic PDF exports in one folder, each page keeping only its latest file")]
public sealed class PagePdfExportSharedFolderMigration() : EmbeddedSchemaMigration("2026101640_page_pdf_export_shared_folder.sql");
public sealed class PagePdfExportMap : ClassMapping<PagePdfExport>
{
    public PagePdfExportMap()
    {
        Table("`PagePdfExport`");
        Id(x => x.PageId, m => { m.Column("`PageId`"); m.Generator(Generators.Assigned); });
        Property(x => x.StorageProfileId); Property(x => x.OwnerId); Property(x => x.Enabled);
        Property(x => x.Folder); Property(x => x.FileName); Property(x => x.KeepVersions); Property(x => x.PublishedOnly); Property(x => x.CoverDetails);
        Property(x => x.SourceOrigin); Property(x => x.Version); Property(x => x.LastRevisionId); Property(x => x.LastConfigurationVersion); Property(x => x.LastProfileVersion);
        Property(x => x.LastExportedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.LastStorageProfileId); Property(x => x.LastFolder); Property(x => x.LastFileName);
        Property(x => x.LastError); Property(x => x.FailureCount);
        Property(x => x.NextAttemptAt, m => m.Type(NHibernateUtil.UtcDateTime)); Property(x => x.RunNowRevisionId);
    }
}
