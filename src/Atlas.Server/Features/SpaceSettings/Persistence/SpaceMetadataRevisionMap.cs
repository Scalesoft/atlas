using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using NHibernate.Type;
using Atlas.Server.Features.SpaceSettings.Domain;

namespace Atlas.Server.Features.SpaceSettings.Persistence;

public sealed class SpaceMetadataRevisionMap : ClassMapping<SpaceMetadataRevision>
{
    public SpaceMetadataRevisionMap()
    {
        Table("`SpaceMetadataRevision`");
        Mutable(false);
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.SpaceId);
        Property(x => x.Name);
        Property(x => x.Icon);
        Property(x => x.Color);
        Property(x => x.Description);
        Property(x => x.PinPolicy, m => m.Type<EnumStringType<SpacePinPolicy>>());
        Property(x => x.CommentsEnabled);
        Property(x => x.PublicationMode, m => m.Type<EnumStringType<Features.Pages.Domain.PublicationMode>>());
        Property(x => x.ChangedBy);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
