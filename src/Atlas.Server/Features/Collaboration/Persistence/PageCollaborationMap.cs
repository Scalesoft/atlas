using FluentMigrator;
using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Collaboration.Domain;

namespace Atlas.Server.Features.Collaboration.Persistence;

[Migration(2026101250, "Refused live snapshot of a page")]
public sealed class CollaborationRefusedSnapshotMigration() : EmbeddedSchemaMigration("2026101250_collaboration_refused_snapshot.sql");

[Migration(2026103035, "The pending log of a live page is counted with its head, not re-read on every update")]
public sealed class CollaborationPendingTotalsMigration() : EmbeddedSchemaMigration("2026103035_collaboration_pending_totals.sql");

public sealed class PageCollaborationMap : ClassMapping<PageCollaboration>
{
    public PageCollaborationMap()
    {
        Table("`PageCollaboration`");
        // Generation changes must not rewrite the multi-megabyte snapshot and projections.
        DynamicUpdate(true);
        Id(x => x.PageId, m =>
        {
            m.Column("`PageId`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.Generation);
        Property(x => x.BaseRevisionId);
        Property(x => x.Sequence);
        Property(x => x.LastSavedSequence);
        Property(x => x.SnapshotSequence);
        Property(x => x.PendingCount);
        Property(x => x.PendingBytes);
        Property(x => x.SchemaBound);
        Property(x => x.SchemaJson, m => m.Type(NHibernateUtil.StringClob));
        // Lazy, each on its own: locking the row, moving its checkpoint or resetting it reads none of them (PRF-S-5), a cold
        // load reads only the snapshot and a live save only the editor JSON. StateSha256 tells whether a snapshot is stored.
        Property(x => x.State, m => { m.Type(NHibernateUtil.BinaryBlob); m.Lazy(true); m.FetchGroup("State"); });
        Property(x => x.StateSha256);
        Property(x => x.ProjectionJson, m => { m.Type(NHibernateUtil.StringClob); m.Lazy(true); m.FetchGroup("ProjectionJson"); });
        Property(x => x.RefusedSequence);
        Property(x => x.Refusal, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.UpdatedAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
