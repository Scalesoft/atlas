namespace Atlas.Server.Features.Auth.Commands;

public sealed record RecordSignIn(Guid UserId, string SecurityStamp, string Method)
{
    /// <summary>The security stamp is a session secret; a failed message must never spell it out in a log.</summary>
    public override string ToString() => "RecordSignIn { sensitive values redacted }";
}
