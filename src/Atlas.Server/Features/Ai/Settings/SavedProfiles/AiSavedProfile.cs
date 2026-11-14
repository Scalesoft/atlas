namespace Atlas.Server.Features.Ai.Settings.SavedProfiles;

/// <summary>A named library entry. Editing its immutable revision never changes the runtime assignment.</summary>
public class AiSavedProfile
{
    public virtual Guid Id { get; set; }
    public virtual Guid ProfileId { get; set; }
    public virtual string Kind { get; set; } = "";
    public virtual string Name { get; set; } = "";
    public virtual Guid RevisionId { get; set; }
    public virtual Guid ChangedById { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
