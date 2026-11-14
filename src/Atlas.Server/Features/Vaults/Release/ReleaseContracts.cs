using Atlas.Server.Features.Vaults.Domain;

namespace Atlas.Server.Features.Vaults.Release;

/// <summary>POST /api/secrets/{id}/release. Fields: any of userName, password, url and notes (Field: one of them, a shorthand).
/// Operation: reveal or copy. ProofId: the fresh re-authentication proof (WP4 V4) when the previous answer was reauth_required (403); one request
/// consumes at most one proof, whatever the number of fields. PageId: the page the reference was shown on; the audit keeps it only when
/// the account can read that page and the page references the secret (Čtenost stránky), otherwise the release is audited without it.</summary>
public sealed record ReleaseSecretRequest(IReadOnlyList<string>? Fields, string? Operation, Guid? ProofId, string? Field = null, Guid? PageId = null);

/// <summary>The released values by field name, returned once over HTTP only (no-store, uncompressed). A requested field that is
/// empty in KeePass is left out. ExpiresInSeconds: how long the client may show them; the server keeps nothing.</summary>
public sealed record SecretValuesView(IReadOnlyDictionary<string, string> Values, int ExpiresInSeconds)
{
    public override string ToString() => $"SecretValuesView {{ {Values.Count} values omitted, ExpiresInSeconds = {ExpiresInSeconds} }}";
}

/// <summary>The answer to a request with the single field shorthand and no fields list.</summary>
public sealed record SecretValueView(string Value, int ExpiresInSeconds)
{
    public override string ToString() => $"SecretValueView {{ value omitted, ExpiresInSeconds = {ExpiresInSeconds} }}";
}

/// <summary>The re-authentication refusal of a release (ReauthRefusal.Release: 403 reauth_required or fresh_auth_unsupported): the
/// effective protection mode, where it comes from and whether the sign-in can prove a fresh login at all (supported false:
/// "nepodporováno", the value is never released).</summary>
public sealed record ReauthChallenge(string Mode, string ModeSource, bool Supported);

/// <summary>POST /api/secrets/{id}/release/report: the client says it showed (reveal) or copied (copy) released values. Audited as a
/// client report, one row per field, never as proof of reading.</summary>
public sealed record ReleaseReportRequest(IReadOnlyList<string>? Fields, string? Operation, string? Field = null);

/// <summary>The API names of the releasable fields and the two operations.</summary>
public static class ReleaseFields
{
    public const string Reveal = "reveal";
    public const string Copy = "copy";

    /// <summary>The registry vocabulary, in this order.</summary>
    public static readonly IReadOnlyList<(string Name, VaultSecretFields Flag)> All =
        [("userName", VaultSecretFields.UserName), ("password", VaultSecretFields.Password), ("url", VaultSecretFields.Url), ("notes", VaultSecretFields.Notes)];

    public static VaultSecretFields Flag(string name) => All.Single(f => f.Name == name).Flag;

    /// <summary>The requested field names, distinct and in registry order; 400 for none or an unknown name.</summary>
    public static IReadOnlyList<string> Parse(IReadOnlyList<string>? fields, string? field)
    {
        var requested = (fields ?? []).Concat(field is null ? [] : [field]).ToArray();
        if (requested.Length is 0 or > 16 || requested.Any(name => All.All(f => f.Name != name)))
        {
            throw new ApiException(400, "Vyberte pole userName, password, url nebo notes.");
        }
        return All.Where(f => requested.Contains(f.Name)).Select(f => f.Name).ToArray();
    }

    public static string RequireOperation(string? operation) => operation is Reveal or Copy
        ? operation
        : throw new ApiException(400, "Operace musí být reveal nebo copy.");
}

/// <summary>Result values of SecretReleaseEvent (release kind).</summary>
public static class ReleaseResults
{
    public const string Released = "released";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string ReauthRequired = "reauth_required";
    public const string Unsupported = "unsupported";
    public const string Unavailable = "unavailable";
}
