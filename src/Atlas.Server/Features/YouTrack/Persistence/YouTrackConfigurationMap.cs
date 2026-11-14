using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.YouTrack.Domain;

namespace Atlas.Server.Features.YouTrack.Persistence;

public sealed class YouTrackConfigurationMap : ClassMapping<YouTrackConfiguration>
{
    public YouTrackConfigurationMap()
    {
        Table("`YouTrackConfiguration`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.Enabled);
        Property(x => x.BaseUrl, m => m.Length(2048));
        Property(x => x.ProtectedToken, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.SpaceProjectsJson, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.ProjectKeysJson, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.ChangedById);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
