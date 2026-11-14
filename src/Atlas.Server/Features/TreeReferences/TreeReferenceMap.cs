using FluentMigrator;
using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Features.TreeReferences;

[Migration(2026102850, "Reference nodes of the space tree")]
public sealed class TreeReferencesMigration() : EmbeddedSchemaMigration("2026102850_tree_references.sql");

public sealed class TreeReferenceMap : ClassMapping<TreeReference>
{
    public TreeReferenceMap()
    {
        Table("`TreeReference`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.SpaceId);
        Property(x => x.ParentPageId);
        Property(x => x.SortOrder);
        Property(x => x.TargetId);
        Property(x => x.Anchor);
        Property(x => x.CreatedBy);
        Property(x => x.CreatedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.IsTrashed);
        Property(x => x.TrashedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.TrashedById);
        Property(x => x.TrashBatchId);
    }
}
