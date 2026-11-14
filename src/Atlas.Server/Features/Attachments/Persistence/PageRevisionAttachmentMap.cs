using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Attachments.Domain;

namespace Atlas.Server.Features.Attachments.Persistence;

/// <remarks>
/// The composite foreign keys (PageId, RevisionId) and (PageId, AttachmentId) stay in the schema only:
/// mapping them as many-to-one would make PageId a shared column of two associations, which NHibernate
/// cannot insert. PageId is a plain property and the database enforces both references.
/// </remarks>
public sealed class PageRevisionAttachmentMap : ClassMapping<PageRevisionAttachment>
{
    public PageRevisionAttachmentMap()
    {
        Table("`PageRevisionAttachment`");
        // Rows are inserted once with a saved revision and never updated.
        Mutable(false);
        ComposedId(m =>
        {
            m.Property(x => x.RevisionId, p => p.Column("`RevisionId`"));
            m.Property(x => x.AttachmentId, p => p.Column("`AttachmentId`"));
        });
        Property(x => x.PageId);
    }
}
