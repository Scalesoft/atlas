using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using FluentMigrator;

namespace Atlas.Server.Features.StorageProfiles.Persistence;

public sealed class StorageProfileMap : ClassMapping<StorageProfile>
{
    public StorageProfileMap()
    {
        Table("`StorageProfile`");
        Id(x => x.Id, m => { m.Column("`Id`"); m.Generator(Generators.Assigned); });
        Property(x => x.Name);
        Property(x => x.Destination);
        Property(x => x.Username);
        Property(x => x.Domain);
        Property(x => x.ProtectedPassword);
        Property(x => x.Enabled);
        Property(x => x.AllowPageExports);
        Property(x => x.AllowBackups);
        Property(x => x.AllSpaces);
        Property(x => x.Version);
    }
}

public sealed class StorageProfileSpaceMap : ClassMapping<StorageProfileSpace>
{
    public StorageProfileSpaceMap()
    {
        Table("`StorageProfileSpace`");
        ComposedId(m =>
        {
            m.Property(x => x.StorageProfileId, p => p.Column("`StorageProfileId`"));
            m.Property(x => x.SpaceId, p => p.Column("`SpaceId`"));
        });
    }
}

[Migration(2026100890)]
public sealed class StorageProfilesMigration() : EmbeddedSchemaMigration("2026100890_storage_profiles.sql");
