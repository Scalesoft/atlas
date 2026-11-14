using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Administration.Domain;

namespace Atlas.Server.Features.Administration.Persistence;

public sealed class GroupMemberViewMap : ClassMapping<GroupMemberView>
{
    public GroupMemberViewMap()
    {
        Mutable(false);
        Subselect("""
            SELECT m."Id", m."GroupId", u."Id" AS "UserId", u."Username", u."DisplayName", u."IsActive", m."ActiveFrom"
            FROM "GroupMember" m
            JOIN "UserAccount" u ON u."Id" = m."UserId"
            WHERE m."ActiveTo" IS NULL
            """);
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.GroupId);
        Property(x => x.UserId);
        Property(x => x.Username);
        Property(x => x.DisplayName);
        Property(x => x.IsActive);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
