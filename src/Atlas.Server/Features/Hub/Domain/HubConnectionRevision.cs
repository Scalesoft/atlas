namespace Atlas.Server.Features.Hub.Domain;

public class HubConnectionRevision
{
    public virtual Guid Id { get; set; }

    public virtual Guid ConnectionId { get; set; }

    public virtual string Issuer { get; set; } = "";

    public virtual string BaseUrl { get; set; } = "";

    public virtual string ProtectedToken { get; set; } = "";

    public virtual HubConnectionMode Mode { get; set; }

    public virtual Guid ChangedById { get; set; }

    public virtual DateTime ActiveFrom { get; set; }

    public virtual DateTime? ActiveTo { get; set; }
}

