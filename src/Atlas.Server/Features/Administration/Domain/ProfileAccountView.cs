namespace Atlas.Server.Features.Administration.Domain;

/// <summary>Self-service account facts: current metadata, credential sources and the last sign-in. Read-only CQRS projection.</summary>
public class ProfileAccountView
{
    public virtual Guid Id { get; set; }
    public virtual string DisplayName { get; set; } = "";
    public virtual string? Email { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual bool HasBuiltinCredential { get; set; }
    public virtual bool HasExternalIdentity { get; set; }
    public virtual DateTime? LastSignInAt { get; set; }
    public virtual string? LastSignInMethod { get; set; }
}
