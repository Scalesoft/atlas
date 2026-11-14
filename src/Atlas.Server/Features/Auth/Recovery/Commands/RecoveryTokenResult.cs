namespace Atlas.Server.Features.Auth.Recovery.Commands;

/// <summary>Envelope: a handler invoked through InvokeAsync&lt;T&gt; must never answer with null.</summary>
public sealed record RecoveryTokenResult(RecoveryTokenFile? File, bool Issued)
{
    public static RecoveryTokenResult None { get; } = new(null, false);
}
