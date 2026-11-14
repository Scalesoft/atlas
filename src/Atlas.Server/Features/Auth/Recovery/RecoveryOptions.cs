namespace Atlas.Server.Features.Auth.Recovery;

/// <summary>
/// Recovery:Username snapshot. A bound administrator is never silently replaced by another one,
/// so the value is read once at startup, like Authentication:Builtin:Enabled.
/// </summary>
public sealed class RecoveryOptions
{
    public string? Username { get; set; }
}
