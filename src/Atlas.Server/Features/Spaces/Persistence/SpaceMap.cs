using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using NHibernate.Type;

namespace Atlas.Server.Features.Spaces.Persistence;

public sealed class SpaceMap : ClassMapping<Space>
{
    public SpaceMap()
    {
        Table("`Space`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.Name);
        Property(x => x.Icon);
        Property(x => x.Color);
        Property(x => x.Description);
        Property(x => x.PinPolicy, m => m.Type<EnumStringType<SpacePinPolicy>>());
        Property(x => x.TrashedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.TrashedById);
        Property(x => x.TrashBatchId);
        Property(x => x.PurgedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.CommentsEnabled);
        Property(x => x.PublicationMode, m => m.Type<EnumStringType<Features.Pages.Domain.PublicationMode>>());
        Property(x => x.VerificationIntervalMonths);
    }
}
