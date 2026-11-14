using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Collaboration.Domain;

namespace Atlas.Server.Features.Collaboration.Persistence;

/// <remarks>
/// No auto-flush: a Subselect has its own query space and mapping by code has no Synchronize(...), so pending
/// inserts into "CollaborationUpdate" are invisible here. Call FlushAsync before reading this projection in a
/// session that has pending writes to the underlying table.
/// Never query it unfiltered either: octet_length detoasts every payload, so always restrict by page and generation.
/// </remarks>
public sealed class CollaborationUpdateSizeMap : ClassMapping<CollaborationUpdateSize>
{
    public CollaborationUpdateSizeMap()
    {
        Mutable(false);
        // Column aliases must match the quoted PascalCase columns produced by AtlasMappings.BeforeMapProperty.
        Subselect("""
            SELECT "PageId", "Generation", "Sequence", octet_length("Payload") AS "Bytes"
            FROM "CollaborationUpdate"
            """);
        ComposedId(id =>
        {
            id.Property(x => x.PageId, m => m.Column("`PageId`"));
            id.Property(x => x.Generation, m => m.Column("`Generation`"));
            id.Property(x => x.Sequence, m => m.Column("`Sequence`"));
        });
        Property(x => x.Bytes);
    }
}
