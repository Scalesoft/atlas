using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.PersonalApi.Domain;

namespace Atlas.Server.Features.PersonalApi.Persistence;

/// <remarks>Mutable on purpose: SetApiAccess closes the current interval through ActiveTo, which is the only
/// column the access-history guard trigger lets an UPDATE touch.</remarks>
public sealed class UserApiPermissionMap : ClassMapping<UserApiPermission>
{
    public UserApiPermissionMap()
    {
        Table("`UserApiPermission`");
        // Closing an interval writes "ActiveTo" alone: the runtime database role may update nothing else (RuntimeDatabaseRole).
        DynamicUpdate(true);
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.UserId);
        Property(x => x.Level);
        // Written from Level on insert, never read back into the entity.
        Property(x => x.CanIssueTokens, m => m.Access(Accessor.ReadOnly));
        Property(x => x.ChangedById);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
