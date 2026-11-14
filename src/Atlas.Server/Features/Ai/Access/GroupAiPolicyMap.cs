using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Features.Ai.Access;

public sealed class GroupAiPolicyMap : ClassMapping<GroupAiPolicy>
{
    public GroupAiPolicyMap()
    {
        Table("`GroupAiPolicy`");
        // Closing an interval writes "ActiveTo" alone: the runtime database role may update nothing else (RuntimeDatabaseRole).
        DynamicUpdate(true);
        Id(x => x.Id, m => { m.Column("`Id`"); m.Generator(Generators.Assigned); });
        Property(x => x.GroupId);
        Property(x => x.Enabled);
        Property(x => x.SearchAnswerDailyLimit);
        Property(x => x.ChatAnswerDailyLimit);
        Property(x => x.PageSummaryDailyLimit);
        Property(x => x.AgentDailyLimit);
        Property(x => x.ToolsEnabled);
        Property(x => x.ChangedById);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}