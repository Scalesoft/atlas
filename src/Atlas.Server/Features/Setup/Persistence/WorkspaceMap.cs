using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Features.Setup.Persistence;

public sealed class WorkspaceMap : ClassMapping<Workspace>
{
    public WorkspaceMap()
    {
        Table("`Workspace`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.Name);
        Property(x => x.CreatedAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
