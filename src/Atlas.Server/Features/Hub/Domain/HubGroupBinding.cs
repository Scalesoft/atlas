namespace Atlas.Server.Features.Hub.Domain;

public class HubGroupBinding
{
    public virtual Guid GroupId { get; set; }

    public virtual Guid ConnectionId { get; set; }

    public virtual string ExternalGroupId { get; set; } = "";

    public virtual bool Enabled { get; set; } = true;

    public virtual bool GrantsWikiAdmin { get; set; }
}

