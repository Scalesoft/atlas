using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Features.Pages.Persistence;

public sealed class PagePublicationMap : ClassMapping<PagePublication>
{
    public PagePublicationMap()
    {
        Table("`PagePublication`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.PageId);
        Property(x => x.RevisionId);
        Property(x => x.PublisherId);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.Channel, m => m.NotNullable(true));
        Property(x => x.ApiTokenId);
        Property(x => x.ChangeSummary);
    }
}
