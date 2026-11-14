namespace Atlas.Server.Features.Auth.OpenIdConnect.Domain;

/// <summary>A provider subject bound to an account. Only the subject binds it: never a name or an e-mail.</summary>
public class ExternalIdentity
{
    public virtual Guid Id { get; set; }

    public virtual Guid UserId { get; set; }

    public virtual string Issuer { get; set; } = "";

    public virtual string Subject { get; set; } = "";

    public virtual DateTime ActiveFrom { get; set; }

    public virtual DateTime? ActiveTo { get; set; }
}
