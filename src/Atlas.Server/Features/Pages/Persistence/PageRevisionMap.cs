using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Features.Pages.Persistence;

public sealed class PageRevisionMap : ClassMapping<PageRevision>
{
    public PageRevisionMap()
    {
        Table("`PageRevision`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.PageId);
        Property(x => x.Version);
        Property(x => x.Title);
        Property(x => x.ContentJson, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.Markdown, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.AuthorId);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.Channel, m => m.NotNullable(true));
        Property(x => x.ApiTokenId);
        Property(x => x.AiAssisted, m => m.NotNullable(true));
        Property(x => x.AiConversationId);
        Property(x => x.GroupId);
        Property(x => x.GroupStartedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.CheckpointCount, m => m.NotNullable(true));
        Property(x => x.IsExplicit, m => m.NotNullable(true));
        Property(x => x.Label);
        Property(x => x.BaseRevisionId);
        Property(x => x.SameAsRevisionId);
        Property(x => x.ChangeSummary, m => m.Type<JsonbText>());
    }
}
