namespace Atlas.Server.Features.Auth.Domain;

/// <summary>An immutable sign-in fact. The writer keeps only the newest hundred rows per account.</summary>
public class UserSignIn
{
    public virtual Guid Id { get; set; }

    public virtual Guid UserId { get; set; }

    public virtual DateTime AuthenticatedAt { get; set; }

    public virtual string AuthenticationMethod { get; set; } = "";
}
