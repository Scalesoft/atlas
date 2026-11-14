using FluentMigrator;
using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Platform.Resources;

[Migration(2026102600, "Content resource identity for pages")]
public sealed class ContentResourceMigration() : EmbeddedSchemaMigration("2026102600_content_resource.sql");

[Migration(2026102700, "Object envelope and the object change log")]
public sealed class ObjectEnvelopeMigration() : EmbeddedSchemaMigration("2026102700_object_envelope.sql");

[Migration(2026102910, "Who trashed an object and the audit of a purge")]
public sealed class ObjectTrashMigration() : EmbeddedSchemaMigration("2026102910_object_trash.sql");

[Migration(2026102930, "Personal items: boards without a space, grants on one object")]
public sealed class PersonalItemsMigration() : EmbeddedSchemaMigration("2026102930_personal_items.sql");

public sealed class ContentResourceMap : ClassMapping<ContentResource>
{
    public ContentResourceMap()
    {
        Table("`ContentResource`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.TypeId);
        Property(x => x.Scope);
        Property(x => x.SpaceId);
        Property(x => x.OwnerId);
        Property(x => x.Version);
        Property(x => x.CreatedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.CreatedBy);
        Property(x => x.UpdatedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.TrashedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.TrashedById);
        Property(x => x.Title);
        Property(x => x.Data, m => m.Type<JsonbText>());
    }
}
