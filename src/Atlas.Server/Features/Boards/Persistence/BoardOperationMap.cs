using NHibernate;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Boards.Domain;

namespace Atlas.Server.Features.Boards.Persistence;

public sealed class BoardOperationMap : ClassMapping<BoardOperation>
{
    public BoardOperationMap()
    {
        Table("`BoardOperation`");
        ComposedId(id =>
        {
            id.Property(x => x.BoardId, m => m.Column("`BoardId`"));
            id.Property(x => x.Sequence, m => m.Column("`Sequence`"));
        });
        Property(x => x.OperationId);
        Property(x => x.AuthorId);
        Property(x => x.AppliedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ChangesJson, m => m.Type(NHibernateUtil.StringClob));
    }
}
