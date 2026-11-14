namespace Atlas.Server.Features.Administration.Contracts;

public sealed record ChangeOwnPassword(string CurrentPassword, string NewPassword)
{
    public override string ToString() => "ChangeOwnPassword { passwords redacted }";
}
