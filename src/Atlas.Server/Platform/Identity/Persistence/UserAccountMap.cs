using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Platform.Identity.Persistence;

public sealed class UserAccountMap : ClassMapping<UserAccount>
{
    public UserAccountMap()
    {
        Table("`UserAccount`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.Username);
        Property(x => x.DisplayName);
        Property(x => x.Email);
        Property(x => x.IsWikiAdmin);
        Property(x => x.OidcAdminIssuer);
        Property(x => x.IsActive);
        Property(x => x.SecurityStamp);
    }
}
