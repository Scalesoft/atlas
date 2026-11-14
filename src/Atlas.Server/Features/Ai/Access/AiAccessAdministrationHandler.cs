using Atlas.Server.Features.Administration;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Access;

public sealed class AiAccessAdministrationHandler(ISession session, AdministrationAccess access, AiSettingsWrite write, AiPolicyResolver resolver)
{
    public async Task<AiAccessView> Handle(GetAiAccess command, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        return await GroupViewAsync(ct);
    }

    private async Task<AiAccessView> GroupViewAsync(CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery("""
            SELECT p."GroupId",g."Name",p."SearchAnswerDailyLimit",p."ChatAnswerDailyLimit",p."PageSummaryDailyLimit",p."AgentDailyLimit",p."ToolsEnabled"
            FROM "GroupAiPolicy" p JOIN "UserGroup" g ON g."Id"=p."GroupId"
            WHERE p."ActiveTo" IS NULL AND p."Enabled" ORDER BY g."Name",g."Id"
            """).ListAsync<object[]>(ct);
        var setting = await CurrentAiSettings.LoadSwitchAsync(session, ct);
        return new(rows.Select(x => new AiGroupGrantView((Guid)x[0], (string)x[1], (int?)x[2], (int?)x[3], (int?)x[4], (int?)x[5], (bool)x[6])).ToArray(),
            setting is null ? null : AdministrationVersion.Format(setting.ActiveFrom));
    }

    public async Task<AiAccessView> Handle(SaveAiAccess command, CancellationToken ct)
    {
        var request = command.Request;
        var actor = await access.LockAdministrationAsync(ct);
        await write.BeginAsync(ct);
        var setting = await CurrentAiSettings.LoadSwitchAsync(session, ct);
        AdministrationVersion.Expect(setting?.ActiveFrom, request.ExpectedVersion);
        var grants = ValidateGroups(request.Groups);
        foreach (var grant in grants)
        {
            if (await session.GetAsync<UserGroup>(grant.GroupId, ct) is null)
                throw new ApiException(404, "Skupina nebyla nalezena.");
        }
        var current = await session.QueryOver<GroupAiPolicy>().Where(x => x.ActiveTo == null).ListAsync(ct);
        var desired = grants.ToDictionary(x => x.GroupId);
        var changed = current.Where(x => x.Enabled || desired.ContainsKey(x.GroupId))
            .Where(x => !Same(x, desired.GetValueOrDefault(x.GroupId))).Select(x => x.GroupId)
            .Concat(desired.Keys.Where(id => current.All(x => x.GroupId != id))).Distinct().ToArray();
        if (changed.Length == 0) return await GroupViewAsync(ct);
        var latest = current.Select(x => x.ActiveFrom).Append(setting?.ActiveFrom ?? DateTime.UnixEpoch).Max();
        var now = Temporal.NextBoundary(latest, DateTime.UtcNow);
        foreach (var policy in current.Where(x => changed.Contains(x.GroupId))) policy.ActiveTo = now;
        if (setting is not null) setting.ActiveTo = now;
        await session.FlushAsync(ct);
        var saved = new List<Guid>();
        foreach (var id in changed)
        {
            var grant = desired.GetValueOrDefault(id);
            saved.Add(Guid.NewGuid());
            await session.SaveAsync(new GroupAiPolicy { Id = saved[^1], GroupId = id, Enabled = grant is not null,
                SearchAnswerDailyLimit = grant?.SearchAnswerDailyLimit, ChatAnswerDailyLimit = grant?.ChatAnswerDailyLimit,
                PageSummaryDailyLimit = grant?.PageSummaryDailyLimit, AgentDailyLimit = grant?.AgentDailyLimit, ToolsEnabled = grant?.ToolsEnabled ?? true,
                ChangedById = actor.Id, ActiveFrom = now }, ct);
        }
        await AccessAuditLog.RecordAsync(session, actor.Id, "ai.group_policy.changed", AccessAuditLog.GroupAiPolicy, saved, ct);
        await session.SaveAsync(new AiSetting { Id = Guid.NewGuid(), Enabled = setting?.Enabled ?? false, ChangedById = actor.Id, ActiveFrom = now }, ct);
        await session.FlushAsync(ct);
        write.Saved();
        return await GroupViewAsync(ct);
    }

    private static bool Same(GroupAiPolicy current, AiGroupGrant? desired)
        => desired is null ? !current.Enabled : current.Enabled && current.SearchAnswerDailyLimit == desired.SearchAnswerDailyLimit
            && current.ChatAnswerDailyLimit == desired.ChatAnswerDailyLimit && current.PageSummaryDailyLimit == desired.PageSummaryDailyLimit
            && current.AgentDailyLimit == desired.AgentDailyLimit && current.ToolsEnabled == desired.ToolsEnabled;

    private static IReadOnlyList<AiGroupGrant> ValidateGroups(IReadOnlyList<AiGroupGrant>? groups)
    {
        if (groups is null || groups.Any(x => x is null) || groups.Select(x => x.GroupId).Distinct().Count() != groups.Count)
            throw new ApiException(400, "Vyberte skupiny bez duplicit.");
        foreach (var group in groups) ValidateLimits(group.SearchAnswerDailyLimit, group.ChatAnswerDailyLimit, group.PageSummaryDailyLimit, group.AgentDailyLimit);
        return groups;
    }

    private static void ValidateLimits(params int?[] limits)
    {
        if (limits.Any(x => x is < 0 or > Settings.AiTaskCatalog.MaxDailyLimit))
            throw new ApiException(400, "Limit musí být od 0 do 10000, nebo výchozí.");
    }

    public async Task<UserAiPolicyView> Handle(GetUserAiPolicy command, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        await RequireUserAsync(command.UserId, ct);
        return await UserViewAsync(command.UserId, ct);
    }

    private async Task RequireUserAsync(Guid userId, CancellationToken ct)
    {
        if (await session.GetAsync<UserAccount>(userId, ct) is null)
            throw new ApiException(404, "Uživatel nebyl nalezen.");
    }

    private async Task<UserAiPolicyView> UserViewAsync(Guid userId, CancellationToken ct)
    {
        var row = await session.QueryOver<UserAiPolicy>().Where(x => x.UserId == userId && x.ActiveTo == null).SingleOrDefaultAsync(ct);
        return new(row?.Enabled, row?.SearchAnswerDailyLimit, row?.ChatAnswerDailyLimit, row?.PageSummaryDailyLimit,
            row is null ? null : AdministrationVersion.Format(row.ActiveFrom), await resolver.ViewAsync(userId, ct), row?.ToolsEnabled, row?.AgentDailyLimit);
    }

    public async Task<UserAiPolicyView> Handle(SaveUserAiPolicy command, CancellationToken ct)
    {
        var (userId, request) = command;
        var actor = await access.LockAdministrationAsync(ct);
        await RequireUserAsync(userId, ct);
        ValidateLimits(request.SearchAnswerDailyLimit, request.ChatAnswerDailyLimit, request.PageSummaryDailyLimit, request.AgentDailyLimit);
        var previous = await session.QueryOver<UserAiPolicy>().Where(x => x.UserId == userId && x.ActiveTo == null).SingleOrDefaultAsync(ct);
        AdministrationVersion.Expect(previous?.ActiveFrom, request.ExpectedVersion);
        if (previous is not null && previous.Enabled == request.Enabled && previous.SearchAnswerDailyLimit == request.SearchAnswerDailyLimit
            && previous.ChatAnswerDailyLimit == request.ChatAnswerDailyLimit && previous.PageSummaryDailyLimit == request.PageSummaryDailyLimit
            && previous.ToolsEnabled == request.ToolsEnabled && previous.AgentDailyLimit == request.AgentDailyLimit)
            return await UserViewAsync(userId, ct);
        var now = Temporal.NextBoundary(previous?.ActiveFrom ?? DateTime.UnixEpoch, DateTime.UtcNow);
        if (previous is not null)
        {
            previous.ActiveTo = now;
            await session.FlushAsync(ct);
        }
        var policy = new UserAiPolicy { Id = Guid.NewGuid(), UserId = userId, Enabled = request.Enabled,
            SearchAnswerDailyLimit = request.SearchAnswerDailyLimit, ChatAnswerDailyLimit = request.ChatAnswerDailyLimit,
            PageSummaryDailyLimit = request.PageSummaryDailyLimit, ToolsEnabled = request.ToolsEnabled, AgentDailyLimit = request.AgentDailyLimit,
            ChangedById = actor.Id, ActiveFrom = now };
        await session.SaveAsync(policy, ct);
        await AccessAuditLog.RecordAsync(session, actor.Id, "ai.user_policy.changed", AccessAuditLog.UserAiPolicy, policy.Id, ct);
        return await UserViewAsync(userId, ct);
    }
}
