namespace Atlas.Server.Features.Security.Reauth;

/// <summary>
/// The one answer to an operation that needs a fresh re-authentication it does not have (owner decision 28. 9.): 403 with code
/// <see cref="RequiredCode"/> (verify again, then retry with the proof) or <see cref="UnsupportedCode"/> (the account's sign-in cannot
/// prove a fresh login, so nothing is changed or released). Security changes, the value release and starting a challenge all refuse
/// through here, so the client handles one status and two codes. 403 rather than 428: all fifteen security-change endpoints and the
/// settings client already answered 403 reauth_required; only the value release used 428 (and reauth_unsupported).
/// </summary>
public static class ReauthRefusal
{
    public const int Status = 403;
    public const string RequiredCode = "reauth_required";
    public const string UnsupportedCode = "fresh_auth_unsupported";

    /// <summary>A security change without a proof and outside the re-authentication window.</summary>
    public static ApiException Required() =>
        new(Status, "Tato akce vyžaduje nové přihlášení. Ověřte se znovu a akci zopakujte.", RequiredCode);

    /// <summary>The account has no method that can prove a fresh login.</summary>
    public static ApiException Unsupported() =>
        new(Status, "Tato akce vyžaduje nové přihlášení, které váš způsob přihlášení neumí doložit. Nic nebylo změněno ani vydáno.", UnsupportedCode);

    public static ApiException For(bool supported) => supported ? Required() : Unsupported();

    /// <summary>A value release the protection mode wants a fresh sign-in for: the same status and codes, and the mode, where it comes
    /// from and whether the sign-in can prove a fresh login as the <c>reauth</c> member of the problem body.</summary>
    public static ApiException Release(string mode, string modeSource, bool supported)
    {
        var reauth = new Dictionary<string, object?> { ["reauth"] = new { mode, modeSource, supported } };
        return supported
            ? new ApiException(Status, "Heslo lze zobrazit až po novém přihlášení.", RequiredCode, reauth)
            : new ApiException(Status, "Heslo vyžaduje nové přihlášení, které váš způsob přihlášení neumí doložit (nepodporováno).", UnsupportedCode, reauth);
    }
}
