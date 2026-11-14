using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Administration.Domain;

namespace Atlas.Server.Features.Administration.Persistence;

public sealed class GroupMemberMap : ClassMapping<GroupMember>
{
    public GroupMemberMap()
    {
        Table("`GroupMember`");
        // Closing an interval writes "ActiveTo" alone: the runtime database role may update nothing else (RuntimeDatabaseRole).
        DynamicUpdate(true);
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.GroupId);
        Property(x => x.UserId);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
