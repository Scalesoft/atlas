using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Setup.Domain;

namespace Atlas.Server.Features.Setup.Persistence;

public sealed class ModuleOnboardingMap : ClassMapping<ModuleOnboarding>
{
    public ModuleOnboardingMap()
    {
        Table("`ModuleOnboarding`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.WorkspaceId);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.CompletedById);
    }
}
