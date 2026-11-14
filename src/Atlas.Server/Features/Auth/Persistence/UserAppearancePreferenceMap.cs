using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Auth.Domain;

namespace Atlas.Server.Features.Auth.Persistence;

public sealed class UserAppearancePreferenceMap : ClassMapping<UserAppearancePreference>
{
    public UserAppearancePreferenceMap()
    {
        Table("`UserAppearancePreference`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.UserId);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.Layout);
        Property(x => x.Accent);
        Property(x => x.Mode);
        Property(x => x.Motion);
        Property(x => x.OpenLocked);
        Property(x => x.DragPages);
        Property(x => x.Language);
    }
}
