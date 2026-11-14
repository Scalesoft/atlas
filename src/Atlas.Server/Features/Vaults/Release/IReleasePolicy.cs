using Microsoft.AspNetCore.Http;
using Atlas.Server.Platform.Identity;

namespace Atlas.Server.Features.Vaults.Release;

/// <summary>Protection decision for releasing a secret value (WP4 V4 implements it; V5 consumes it).</summary>
public interface IReleasePolicy
{
    /// <summary>Effective mode for the secret and whether this request needs a fresh re-authentication.</summary>
    Task<ReleaseDecision> EvaluateAsync(UserAccount actor, Guid secretId, HttpContext http, CancellationToken ct);

    /// <summary>Atomically consumes a verified, unexpired, unused proof bound to actor, session, secret and operation.</summary>
    Task<bool> TryConsumeProofAsync(UserAccount actor, HttpContext http, Guid secretId, string operation, Guid proofId, CancellationToken ct);
}

/// <param name="Mode">"never" | "outsideTrustedNetworks" | "always".</param>
/// <param name="ModeSource">"instance" | "vault" | "secret".</param>
/// <param name="RequiresReauth">True when this request must present a consumed proof.</param>
/// <param name="FreshAuthSupported">False when the actor's sign-in method cannot prove a fresh login ("nepodporováno").</param>
/// <param name="MatchedNetwork">Name of the trusted network that granted the exception, if any.</param>
public sealed record ReleaseDecision(string Mode, string ModeSource, bool RequiresReauth, bool FreshAuthSupported, string? MatchedNetwork);
