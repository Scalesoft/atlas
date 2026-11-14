namespace Atlas.Server.Features.Setup.Domain;

/// <summary>
/// A pending module walkthrough for the installation. Setup opens it; ModuleSettings closes it by
/// setting ActiveTo together with CompletedById (the database enforces that pairing).
/// </summary>
public class ModuleOnboarding
{
    public virtual Guid Id { get; set; }

    public virtual int WorkspaceId { get; set; }

    public virtual DateTime ActiveFrom { get; set; }

    public virtual DateTime? ActiveTo { get; set; }

    public virtual Guid? CompletedById { get; set; }
}
