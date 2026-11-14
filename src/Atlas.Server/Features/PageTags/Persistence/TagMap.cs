using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.PageTags.Domain;

namespace Atlas.Server.Features.PageTags.Persistence;

public sealed class TagMap : ClassMapping<Tag>
{
    public TagMap()
    {
        Table("`Tag`");
        Mutable(false);
        Id(x => x.Id, m => { m.Generator(Generators.Assigned); m.Column("`Id`"); });
        Property(x => x.Name, m => { m.Length(48); m.NotNullable(true); });
        Property(x => x.NormalizedName, m => { m.Length(48); m.NotNullable(true); });
        Property(x => x.CreatedAt, m => { m.Type(NHibernateUtil.UtcDateTime); m.NotNullable(true); });
        Property(x => x.CreatedById, m => m.NotNullable(true));
    }
}
