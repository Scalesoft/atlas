using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.PersonalLibrary.Domain;

namespace Atlas.Server.Features.PersonalLibrary.Persistence;

public sealed class PageContributionMap : ClassMapping<PageContribution>
{
    public PageContributionMap()
    {
        Table("`PageContribution`");
        Mutable(false);
        ComposedId(m =>
        {
            m.Property(x => x.UserId, p => p.Column("`UserId`"));
            m.Property(x => x.PageId, p => p.Column("`PageId`"));
        });
        Property(x => x.LastRevisionVersion);
    }
}
