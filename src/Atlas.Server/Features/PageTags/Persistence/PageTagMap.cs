using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.PageTags.Domain;

namespace Atlas.Server.Features.PageTags.Persistence;

public sealed class PageTagMap : ClassMapping<PageTag>
{
    public PageTagMap()
    {
        Table("`PageTag`");
        Id(x => x.Id, m => { m.Generator(Generators.Assigned); m.Column("`Id`"); });
        Property(x => x.PageId, m => m.NotNullable(true));
        Property(x => x.TagId, m => m.NotNullable(true));
        Property(x => x.CreatedById, m => m.NotNullable(true));
        Property(x => x.CreatedAt, m => { m.Type(NHibernateUtil.UtcDateTime); m.NotNullable(true); });
        Property(x => x.ActiveFrom, m => { m.Type(NHibernateUtil.UtcDateTime); m.NotNullable(true); });
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
