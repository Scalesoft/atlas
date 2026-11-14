using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Ai.Settings.Domain;

namespace Atlas.Server.Features.Ai.Settings.Persistence;

public sealed class AiFeatureSettingsMap : ClassMapping<AiFeatureSettings>
{
    public AiFeatureSettingsMap()
    {
        Table("`AiFeatureSettings`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.TasksJson, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.ChangedById);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
