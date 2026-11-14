namespace Atlas.Server.Features.Setup.Domain;

/// <summary>The single installation row: the wiki is configured once this exists.</summary>
public class Workspace
{
    public virtual int Id { get; set; } = 1;
    public virtual string Name { get; set; } = "";
    public virtual DateTime CreatedAt { get; set; }
}
