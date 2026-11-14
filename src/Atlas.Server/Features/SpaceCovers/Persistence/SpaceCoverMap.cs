using FluentMigrator;
using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using NHibernate.Type;
using Atlas.Server.Features.SpaceCovers.Domain;

namespace Atlas.Server.Features.SpaceCovers.Persistence;

[Migration(2026100910, "Space card images")]
public sealed class SpaceCoverMigration() : EmbeddedSchemaMigration("2026100910_space_covers.sql");

[Migration(2026101142, "Space image source and catalog key")]
public sealed class SpaceCoverSourceMigration() : EmbeddedSchemaMigration("2026101142_space_cover_source.sql");

public sealed class SpaceCoverMap : ClassMapping<SpaceCover>
{
    public SpaceCoverMap()
    {
        Table("`SpaceCover`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.SpaceId);
        Property(x => x.Source, m => m.Type<EnumStringType<SpaceCoverSource>>());
        Property(x => x.CatalogKey);
        Property(x => x.FileId);
        Property(x => x.MimeType);
        Property(x => x.Size);
        Property(x => x.Sha256);
        Property(x => x.Tinted);
        Property(x => x.ChangedBy);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
