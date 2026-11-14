using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Features.Assistant.Persistence;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Access;

public sealed record AiResolvedLimit(string Task, int? Effective, string Source, string Origin, Guid? GroupId = null, string? GroupName = null);
/// <param name="ToolsEnabled">The "AI s nástroji" right (D17): <c>Enabled && (personal ?? (any enabled grant ? any grant with tools : true))</c>.</param>
public sealed record AiResolvedPolicy(bool Enabled, IReadOnlyList<AiResolvedLimit> Limits, bool ToolsEnabled = false)
{
    public int? Limit(string task) => Limits.Single(x => x.Task == task).Effective;
}

/// <summary>Fresh scalar reads deliberately bypass the session identity map between preparation and release.</summary>
public sealed class AiPolicyResolver(ISession session, ConversationStore conversations, TimeProvider clock)
{
    public static readonly string[] LimitTasks = [AiTaskCatalog.SearchAnswer, AiTaskCatalog.ChatAnswer, AiTaskCatalog.PageSummary, AiTaskCatalog.AssistantAgent];

    public async Task<AiResolvedPolicy> ResolveAsync(Guid userId, CancellationToken ct)
    {
        var global = await session.CreateSQLQuery("""SELECT "Enabled" FROM "AiSetting" WHERE "ActiveTo" IS NULL""").UniqueResultAsync<bool?>(ct) ?? false;
        var json = await session.CreateSQLQuery("""SELECT "TasksJson" FROM "AiFeatureSettings" WHERE "ActiveTo" IS NULL""").UniqueResultAsync<string>(ct);
        var defaults = CurrentAiSettings.ResolveTasks(json);
        var personal = await session.CreateSQLQuery("""
            SELECT "Enabled","SearchAnswerDailyLimit","ChatAnswerDailyLimit","PageSummaryDailyLimit","AgentDailyLimit","ToolsEnabled"
            FROM "UserAiPolicy" WHERE "UserId"=:id AND "ActiveTo" IS NULL
            """).SetParameter("id", userId).UniqueResultAsync<object[]>(ct);
        var groups = await session.CreateSQLQuery("""
            SELECT DISTINCT p."GroupId",g."Name",p."SearchAnswerDailyLimit",p."ChatAnswerDailyLimit",p."PageSummaryDailyLimit",p."AgentDailyLimit",p."ToolsEnabled"
            FROM "EffectiveGroupMember" m JOIN "GroupAiPolicy" p ON p."GroupId"=m."GroupId" AND p."ActiveTo" IS NULL AND p."Enabled"
            JOIN "UserGroup" g ON g."Id"=p."GroupId" WHERE m."UserId"=:id ORDER BY p."GroupId"
            """).SetParameter("id", userId).ListAsync<object[]>(ct);
        // A limit task without a catalog row yet (assistant.agent before its feature row) has no wiki default.
        var limits = LimitTasks.Select((task, i) => ResolveLimit(task, defaults.TryGetValue(task, out var setting) ? setting.DailyLimit : null, i, personal, groups)).ToArray();
        var enabled = global && ((bool?)personal?[0] ?? groups.Count > 0);
        // D20: without an enabled grant (AI enabled personally only) tools default to on; grants decide when there are any.
        var tools = (bool?)personal?[LimitTasks.Length + 1] ?? (groups.Count == 0 || groups.Any(group => (bool)group[LimitTasks.Length + 2]));
        return new(enabled, limits, enabled && tools);
    }

    private static AiResolvedLimit ResolveLimit(string task, int? global, int index, object[]? personal, IList<object[]> groups)
    {
        var result = new AiResolvedLimit(task, global, "default", "global");
        var first = true;
        foreach (var group in groups)
        {
            var candidate = FromOverride(task, (int?)group[index + 2], global, "group", (Guid)group[0], (string)group[1]);
            if (first || Greater(candidate.Effective, result.Effective))
            {
                result = candidate;
                first = false;
            }
        }
        return personal?[index + 1] is int value ? FromOverride(task, value, global, "user") : result;
    }

    private static bool Greater(int? candidate, int? current) => current is not null && (candidate is null || candidate > current);
    private static AiResolvedLimit FromOverride(string task, int? value, int? global, string origin, Guid? groupId = null, string? groupName = null)
        => new(task, value == 0 ? null : value ?? global, value is null ? "default" : value == 0 ? "unlimited" : "custom", origin, groupId, groupName);

    public async Task<MyAiPolicyView> ViewAsync(Guid userId, CancellationToken ct)
    {
        var policy = await ResolveAsync(userId, ct);
        var (from, to) = AiQuotaDay.Bounds(clock);
        var limits = new List<AiLimitView>();
        foreach (var limit in policy.Limits)
        {
            var used = await conversations.DailyCountAsync(userId, limit.Task, from, to, ct);
            limits.Add(new(limit.Task, limit.Effective, used, limit.Effective is { } maximum ? Math.Max(0, maximum - used) : null,
                to, limit.Source, limit.Origin, limit.GroupId, limit.GroupName));
        }
        return new(policy.Enabled, limits, policy.ToolsEnabled);
    }
}

public static class AiQuotaDay
{
    public static (DateTime From, DateTime To) Bounds(TimeProvider clock)
        => Bounds(clock.GetUtcNow().UtcDateTime);

    public static (DateTime From, DateTime To) Bounds(DateTime utcNow)
        => (utcNow.Date, utcNow.Date.AddDays(1));
}
