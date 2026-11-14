namespace Atlas.Server.Features.Ai.Access;

/// <param name="ToolsEnabled">The "AI s nástroji" right of the group's members (D17); on unless the admin turns it off (D20).</param>
public sealed record AiGroupGrant(Guid GroupId, int? SearchAnswerDailyLimit, int? ChatAnswerDailyLimit, int? PageSummaryDailyLimit,
    int? AgentDailyLimit = null, bool ToolsEnabled = true);
public sealed record AiGroupGrantView(Guid GroupId, string GroupName, int? SearchAnswerDailyLimit, int? ChatAnswerDailyLimit, int? PageSummaryDailyLimit,
    int? AgentDailyLimit, bool ToolsEnabled);
public sealed record AiAccessView(IReadOnlyList<AiGroupGrantView> Groups, string? Version);
public sealed record SaveAiAccessRequest(IReadOnlyList<AiGroupGrant>? Groups, string? ExpectedVersion);
/// <param name="ToolsEnabled">The personal "AI s nástroji" exception (D17): null inherits from the groups.</param>
public sealed record SaveUserAiPolicyRequest(bool? Enabled, int? SearchAnswerDailyLimit, int? ChatAnswerDailyLimit, int? PageSummaryDailyLimit, string? ExpectedVersion,
    bool? ToolsEnabled = null, int? AgentDailyLimit = null);
public sealed record AiLimitView(string Task, int? Effective, long Used, long? Remaining, DateTime ResetAt, string Source, string Origin, Guid? GroupId, string? GroupName);
public sealed record MyAiPolicyView(bool Enabled, IReadOnlyList<AiLimitView> Limits, bool ToolsEnabled);
public sealed record UserAiPolicyView(bool? Enabled, int? SearchAnswerDailyLimit, int? ChatAnswerDailyLimit, int? PageSummaryDailyLimit, string? Version, MyAiPolicyView Resolved,
    bool? ToolsEnabled, int? AgentDailyLimit);
public sealed record GetAiAccess;
public sealed record SaveAiAccess(SaveAiAccessRequest Request);
public sealed record GetUserAiPolicy(Guid UserId);
public sealed record SaveUserAiPolicy(Guid UserId, SaveUserAiPolicyRequest Request);
public sealed record GetMyAiPolicy;
public sealed record ReadAiPermission;
