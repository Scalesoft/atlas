namespace Atlas.Server.Features.Auth.OpenIdConnect.Domain;

/// <summary>Operational state of the OpenIddict client. Version carries the one-shot nature of a state token.</summary>
public class OidcStateToken
{
    public virtual string Id { get; set; } = Guid.NewGuid().ToString("N");

    public virtual int Version { get; set; }

    public virtual string? ApplicationId { get; set; }

    public virtual string? AuthorizationId { get; set; }

    public virtual DateTime? CreationDate { get; set; }

    public virtual DateTime? ExpirationDate { get; set; }

    public virtual DateTime? RedemptionDate { get; set; }

    public virtual string? Payload { get; set; }

    public virtual string? Properties { get; set; }

    public virtual string? ReferenceId { get; set; }

    public virtual string? Status { get; set; }

    public virtual string? Subject { get; set; }

    public virtual string? Type { get; set; }
}
