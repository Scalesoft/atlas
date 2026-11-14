using FluentMigrator;
using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Contacts.Domain;

namespace Atlas.Server.Features.Contacts.Persistence;

[Migration(2026110530, "The small picture of a contact")]
public sealed class ContactPhotoMigration() : EmbeddedSchemaMigration("2026110530_contact_photo.sql");

public sealed class ContactPhotoMap : ClassMapping<ContactPhoto>
{
    public ContactPhotoMap()
    {
        Table("`ContactPhoto`");
        Id(x => x.ContactId, m =>
        {
            m.Column("`ContactId`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.MimeType);
        Property(x => x.Sha256);
        Property(x => x.Content, m => m.Type(NHibernateUtil.BinaryBlob));
    }
}
