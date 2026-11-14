using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.PersonalLibrary.Domain;

namespace Atlas.Server.Features.PersonalLibrary.Persistence;

public sealed class PageVisitMap : ClassMapping<PageVisit>
{
    public PageVisitMap()
    {
        Table("`PageVisit`");
        Id(x => x.Id, m =>
        {
            m.Generator(Generators.Assigned);
            m.Column("`Id`");
        });
        Property(x => x.UserId);
        Property(x => x.PageId);
        Property(x => x.LastVisitedAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
