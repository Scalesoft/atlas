using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Features.Ai.Settings.SavedProfiles;

public sealed class AiSavedProfileMap : ClassMapping<AiSavedProfile>
{
    public AiSavedProfileMap()
    {
        Table("`AiSavedProfile`");
        Id(x => x.Id, m => { m.Column("`Id`"); m.Generator(Generators.Assigned); });
        Property(x => x.ProfileId);
        Property(x => x.Kind, m => m.Length(16));
        Property(x => x.Name, m => m.Length(200));
        Property(x => x.RevisionId);
        Property(x => x.ChangedById);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
