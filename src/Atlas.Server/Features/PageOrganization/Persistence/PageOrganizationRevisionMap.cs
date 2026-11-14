using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.PageOrganization.Domain;

namespace Atlas.Server.Features.PageOrganization.Persistence;

public sealed class PageOrganizationRevisionMap : ClassMapping<PageOrganizationRevision>
{
    public PageOrganizationRevisionMap()
    {
        Table("`PageOrganizationRevision`");
        Mutable(false);
        Id(x => x.Id, m =>
        {
            m.Generator(Generators.Assigned);
            m.Column("`Id`");
        });
        Property(x => x.PageId);
        Property(x => x.SpaceId);
        Property(x => x.ParentId);
        Property(x => x.SortOrder);
        Property(x => x.IsTrashed);
        Property(x => x.TrashedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.TrashedById);
        Property(x => x.TrashBatchId);
        Property(x => x.ActorId);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
