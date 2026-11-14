namespace Atlas.Server.Features.Security.Domain;

/// <summary>What a re-authentication is bound to: the global security configuration (instance), one vault, one secret, or nothing
/// (the provider self-test).</summary>
public sealed record ReauthTarget(string Kind, Guid? Id)
{
    public const string InstanceKind = "instance";
    public const string VaultKind = "vault";
    public const string SecretKind = "secret";
    public const string NoneKind = "none";

    public static ReauthTarget Instance { get; } = new(InstanceKind, null);
    public static ReauthTarget None { get; } = new(NoneKind, null);

    public static ReauthTarget Vault(Guid id) => new(VaultKind, id);

    public static ReauthTarget Secret(Guid id) => new(SecretKind, id);

    /// <summary>The proof scope of a security change: vault.* and secret.* kinds are bound to their target; every other kind
    /// (instance protection, networks, proxies, the hop limit, space and page manage grants, the WikiAdmin role, revoke-all) to the
    /// global security configuration, so a proof started without a target covers them.</summary>
    public static ReauthTarget Of(SecurityChange change)
    {
        if (change.Kind.StartsWith(VaultKind + ".", StringComparison.Ordinal))
        {
            return Vault(change.TargetId);
        }
        return change.Kind.StartsWith(SecretKind + ".", StringComparison.Ordinal) ? Secret(change.TargetId) : Instance;
    }
}
