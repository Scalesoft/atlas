using System.Text.RegularExpressions;

namespace Atlas.Server.Features.ApiActivity;

/// <summary>The outcome code of an audited write: ok, the machine-readable code of the refusal (scope_missing, version_conflict,
/// collaboration_unsaved, …), or one derived from its status. Never the message.</summary>
public static partial class ApiActivityOutcomes
{
    public const string Ok = "ok";
    public const string ScopeMissing = "scope_missing";
    public const string Invalid = "invalid";
    public const string NotFound = "not_found";
    public const string Cancelled = "cancelled";
    public const string Error = "error";

    public static string From(ApiException exception)
        => exception.Code is { } code && IsValid(code) ? code : ForStatus(exception.Status);

    public static string ForStatus(int status) => status switch
    {
        < 400 => Ok,
        400 or 422 => Invalid,
        401 => "unauthenticated",
        403 => "forbidden",
        404 => NotFound,
        409 => "conflict",
        410 => "gone",
        413 => "too_large",
        415 => "unsupported_media",
        423 => "locked",
        429 => "rate_limited",
        503 => "unavailable",
        _ => Error
    };

    /// <summary>What the "Outcome" column accepts.</summary>
    public static bool IsValid(string outcome) => OutcomePattern().IsMatch(outcome);

    [GeneratedRegex("^[a-z_]{2,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex OutcomePattern();
}
