namespace Atlas.Server.Features.Vaults.Release;

/// <summary>
/// The release policy until WP4 V4 registers the real one (TryAddScoped, so V4's registration wins): every release requires a
/// fresh re-authentication that no sign-in can prove, so no value is ever released. Fail closed by design.
/// </summary>
public sealed class FailClosedReleasePolicy : IReleasePolicy
{
    public Task<ReleaseDecision> EvaluateAsync(UserAccount actor, Guid secretId, HttpContext http, CancellationToken ct)
        => Task.FromResult(new ReleaseDecision("always", "instance", RequiresReauth: true, FreshAuthSupported: false, null));

    public Task<bool> TryConsumeProofAsync(UserAccount actor, HttpContext http, Guid secretId, string operation, Guid proofId, CancellationToken ct)
        => Task.FromResult(false);
}
