namespace Atlas.Server.Features.Auth.Domain;

public class BuiltinCredential
{
    public virtual Guid UserId { get; set; }

    public virtual string? PasswordHash { get; set; }
}
