using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.PersonalLibrary.Domain;

namespace Atlas.Server.Features.PersonalLibrary.Persistence;

public sealed class PageFavoriteMap : ClassMapping<PageFavorite>
{
    public PageFavoriteMap()
    {
        Table("`PageFavorite`");
        Id(x => x.Id, m =>
        {
            m.Generator(Generators.Assigned);
            m.Column("`Id`");
        });
        Property(x => x.UserId);
        Property(x => x.PageId);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
