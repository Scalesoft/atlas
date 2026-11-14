using Atlas.Server.Features.Objects.Domain;
using NHibernate;
using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Features.Objects.Persistence;

/// <summary>Table of migration 2026102700 (Platform/Resources, with the envelope); backed up by Objects.</summary>
public sealed class ObjectChangeMap : ClassMapping<ObjectChange>
{
    public ObjectChangeMap()
    {
        Table("`ObjectChange`");
        ComposedId(id =>
        {
            id.Property(x => x.ObjectId, m => m.Column("`ObjectId`"));
            id.Property(x => x.Version, m => m.Column("`Version`"));
        });
        Property(x => x.ChangeId);
        Property(x => x.AuthorId);
        Property(x => x.AppliedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.PayloadJson, m => m.Type<JsonbText>());
    }
}
