using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.PageSource.Domain;

namespace Atlas.Server.Features.PageSource.Persistence;

/// <summary>Writes one kind of PageAccessEvent row; OccurredAt and ShareLinkId keep their database defaults. Immutable: insert only.</summary>
public sealed class PageSourceEditEventMap : ClassMapping<PageSourceEditEvent>
{
    public PageSourceEditEventMap()
    {
        Table("`PageAccessEvent`");
        Mutable(false);
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Identity);
        });
        Property(x => x.PageId);
        Property(x => x.RevisionId);
        Property(x => x.ActorId);
        Property(x => x.Action);
    }
}
