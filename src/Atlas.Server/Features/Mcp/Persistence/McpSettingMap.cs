using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Mcp.Domain;

namespace Atlas.Server.Features.Mcp.Persistence;

public sealed class McpSettingMap : ClassMapping<McpSetting>
{
    public McpSettingMap()
    {
        Table("`McpSetting`");
        // Closing an interval writes "ActiveTo" alone: the runtime database role may update nothing else (RuntimeDatabaseRole).
        DynamicUpdate(true);
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.Enabled);
        Property(x => x.ChangedById);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
