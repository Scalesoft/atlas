namespace Atlas.Server.Features.Administration.Contracts;

/// <summary>The first local password of an account that signs in elsewhere (Hub). <see cref="Username"/> is read only while the
/// account has no sign-in name of its own yet.</summary>
public sealed record SetOwnLocalPassword(string? Username, string NewPassword)
{
    public override string ToString() => "SetOwnLocalPassword { password redacted }";
}
