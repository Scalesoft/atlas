using FluentMigrator;
using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Projector.Domain;

namespace Atlas.Server.Features.Projector.Persistence;

[Migration(2026102800, "Projector: catalog pages and their audit, and the requirement index of page content")]
public sealed class ProjectorMigration() : EmbeddedSchemaMigration("2026102800_projector.sql");

/// <summary>ProjectorCatalogPage of migration 2026102800; the requirement index is written by its triggers and read by SQL.</summary>
public sealed class ProjectorCatalogPageMap : ClassMapping<ProjectorCatalogPage>
{
    public ProjectorCatalogPageMap()
    {
        Table("`ProjectorCatalogPage`");
        Id(x => x.PageId, m =>
        {
            m.Column("`PageId`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.MarkedById);
        Property(x => x.MarkedAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
