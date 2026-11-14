namespace Atlas.Server.Features.Vaults;

/// <summary>How a vault or secret decision ends, as PageAccessOutcome: Missing when the account may not even learn that the
/// target exists (404; also an archived vault or a removed secret), Forbidden when it may Discover it but its level is below the
/// required one (403), Allowed otherwise.</summary>
public enum VaultAccessOutcome
{
    Missing,
    Forbidden,
    Allowed
}

/// <summary>
/// One vault or secret decision, made by <see cref="VaultGrantSnapshot"/>. Level is the account's effective level on the target.
/// The source: WikiAdmin (manage everywhere, unbounded), SpaceAdmin when the account is a space Admin of the vault's space, GrantIds
/// the SpaceGrant and ResourceGrant rows that reach the required level, and Until the latest end of those rows (null when one is
/// unbounded), which bounds what a manager may delegate.
/// </summary>
public sealed record VaultAuthorization(VaultAccessOutcome Outcome, VaultAccessLevel Required, VaultAccessLevel Level,
    bool SpaceAdmin, bool WikiAdmin, IReadOnlyList<Guid> GrantIds, DateTime? Until)
{
    public bool Allowed => Outcome == VaultAccessOutcome.Allowed;

    public static VaultAuthorization Missing(VaultAccessLevel required)
    {
        return new VaultAuthorization(VaultAccessOutcome.Missing, required, VaultAccessLevel.None, false, false, [], null);
    }

    /// <summary>The standard answer: 404 when Missing, 403 when Forbidden, this decision when allowed.</summary>
    public VaultAuthorization Require()
    {
        return Outcome switch
        {
            VaultAccessOutcome.Allowed => this,
            VaultAccessOutcome.Forbidden => throw new ApiException(403, Required == VaultAccessLevel.Manage
                ? "K této operaci potřebujete oprávnění spravovat trezor nebo heslo."
                : "K této operaci potřebujete oprávnění číst heslo."),
            _ => throw new ApiException(404, "Trezor nebo heslo nebylo nalezeno.")
        };
    }
}
