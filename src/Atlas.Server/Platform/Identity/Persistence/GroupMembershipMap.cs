using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Platform.Identity.Persistence;

public sealed class GroupMembershipMap : ClassMapping<GroupMembership>
{
    public GroupMembershipMap()
    {
        Subselect("SELECT \"Id\", \"GroupId\", \"UserId\", \"ActiveFrom\", \"ActiveTo\", \"VerifiedUntil\" FROM \"EffectiveGroupMember\"");
        Synchronize("GroupMember", "UserAccount", "ExternalIdentity", "HubIdentityState", "HubConnectionRevision", "HubGroupBinding");
        Mutable(false);
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.GroupId);
        Property(x => x.UserId);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.VerifiedUntil, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
