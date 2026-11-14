namespace Atlas.Server.Features.Ai.Settings.Domain;

/// <summary>The current revision of one profile kind, temporal like <c>EditorAssistSetting</c>. Its ActiveFrom is the
/// compare-and-swap version of the profile section; no current row means the kind is not configured.</summary>
public class AiProfileAssignment
{
    public virtual Guid Id { get; set; }
    public virtual string Kind { get; set; } = "";
    public virtual Guid RevisionId { get; set; }
    public virtual Guid ChangedById { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
