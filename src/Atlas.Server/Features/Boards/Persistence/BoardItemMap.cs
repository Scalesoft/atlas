using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Boards.Domain;

namespace Atlas.Server.Features.Boards.Persistence;

public sealed class BoardItemMap : ClassMapping<BoardItem>
{
    public BoardItemMap()
    {
        Table("`BoardItem`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.BoardId);
        Property(x => x.Kind);
        Property(x => x.X);
        Property(x => x.Y);
        Property(x => x.Width);
        Property(x => x.Height);
        Property(x => x.Z);
        Property(x => x.Text);
        Property(x => x.Color);
        Property(x => x.TargetResourceId);
        Property(x => x.FromItemId);
        Property(x => x.ToItemId);
        Property(x => x.Version);
        Property(x => x.IsDeleted);
        Property(x => x.ParentId);
        Property(x => x.PropsJson, m => m.Type<JsonbText>());
        Property(x => x.SearchText, m => m.Type(NHibernate.NHibernateUtil.StringClob));
    }
}
