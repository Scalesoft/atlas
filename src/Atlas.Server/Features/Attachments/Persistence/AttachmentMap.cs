using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Attachments.Domain;

namespace Atlas.Server.Features.Attachments.Persistence;

public sealed class AttachmentMap : ClassMapping<Attachment>
{
    public AttachmentMap()
    {
        Table("`Attachment`");
        Id(x => x.Id, m =>
        {
            m.Generator(Generators.Assigned);
            m.Column("`Id`");
        });
        Property(x => x.PageId);
        Property(x => x.UploadedBy);
        Property(x => x.Name);
        Property(x => x.MimeType);
        Property(x => x.Size);
        Property(x => x.Sha256);
        Property(x => x.IsImage);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
