namespace Atlas.Server.Features.Ai.Access;

public class UserAiPolicy
{
    public virtual Guid Id { get; set; }
    public virtual Guid UserId { get; set; }
    public virtual bool? Enabled { get; set; }
    public virtual int? SearchAnswerDailyLimit { get; set; }
    public virtual int? ChatAnswerDailyLimit { get; set; }
    public virtual int? PageSummaryDailyLimit { get; set; }
    public virtual int? AgentDailyLimit { get; set; }
    public virtual bool? ToolsEnabled { get; set; }
    public virtual Guid ChangedById { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}