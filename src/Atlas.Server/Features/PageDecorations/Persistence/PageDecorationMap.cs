using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.PageDecorations.Domain;

namespace Atlas.Server.Features.PageDecorations.Persistence;

public sealed class PageDecorationMap : ClassMapping<PageDecoration>
{
    public PageDecorationMap()
    {
        Table("`PageDecoration`");
        Id(x => x.PageId, m => { m.Generator(Generators.Assigned); m.Column("`PageId`"); });
        Property(x => x.RevisionId, m => m.NotNullable(true));
        Property(x => x.Icon, m => m.Length(40));
        Property(x => x.Color, m => m.Length(16));
    }
}
