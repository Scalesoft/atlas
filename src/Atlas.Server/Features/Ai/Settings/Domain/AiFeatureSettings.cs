namespace Atlas.Server.Features.Ai.Settings.Domain;

/// <summary>The switches of the AI tasks as one temporal revision. <see cref="TasksJson"/> is
/// <c>{ "&lt;task&gt;": { "enabled": bool, "cloudWorkingText": bool } }</c>; a task it does not mention takes its
/// <see cref="AiTaskCatalog"/> default.</summary>
public class AiFeatureSettings
{
    public virtual Guid Id { get; set; }
    public virtual string TasksJson { get; set; } = "{}";
    public virtual Guid ChangedById { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
