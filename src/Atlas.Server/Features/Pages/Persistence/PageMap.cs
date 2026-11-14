using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Features.Pages.Persistence;

public sealed class PageMap : ClassMapping<Page>
{
    public PageMap()
    {
        Table("`Page`");
        // Dirty updates must touch only changed columns: Page rows carry the published pointer used by search triggers.
        DynamicUpdate(true);
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.SpaceId);
        Property(x => x.ParentId);
        Property(x => x.OwnerId);
        Property(x => x.Version);
        Property(x => x.PublishedRevisionId);
        Property(x => x.SortOrder);
        Property(x => x.IsTrashed);
        Property(x => x.IsTemplate);
        Property(x => x.TrashedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.TrashedById);
        Property(x => x.TrashBatchId);
        Property(x => x.PurgedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.OwnAccess);
        Property(x => x.TemplatePageId);
        Property(x => x.PublicationMode, m => m.Type<NHibernate.Type.EnumStringType<PublicationMode>>());
        Property(x => x.SortChildrenByTitle);
    }
}
