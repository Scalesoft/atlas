using System.Globalization;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PageVerifications.Domain;

namespace Atlas.Server.Features.PageVerifications;

/// <summary>The verification as MCP and the assistant read it (plan §7): one Czech line and the structured block.</summary>
public static class PageVerificationText
{
    /// <summary>Null when verification is off: nothing is said.</summary>
    public static string? Line(PageVerificationView view) => view.State switch
    {
        PageVerificationStates.Verified => $"ověřeno do {Date(view.VerifiedUntil)}{Steward(view)}{Changed(view)}",
        PageVerificationStates.Expiring => $"ověření brzy vyprší ({Date(view.VerifiedUntil)}){Steward(view)}{Changed(view)}",
        // The assistant says so in its answer (plan §7): the source was not verified since then.
        PageVerificationStates.Expired => $"OVĚŘENÍ VYPRŠELO {Date(view.VerifiedUntil)}: zdroj nebyl ověřen od {Date(view.VerifiedAt)}; " +
            $"v odpovědi to uveďte{Steward(view)}{Changed(view)}",
        PageVerificationStates.Unverified => $"neověřeno{Steward(view)}",
        _ => null
    };

    /// <summary>A state alone (a search hit).</summary>
    public static string State(string state) => state switch
    {
        PageVerificationStates.Verified => "ověřeno",
        PageVerificationStates.Expiring => "ověření brzy vyprší",
        PageVerificationStates.Expired => "OVĚŘENÍ VYPRŠELO (v odpovědi uveďte, že zdroj není ověřený)",
        PageVerificationStates.Unverified => "neověřeno",
        _ => state
    };

    public static JsonObject Json(PageVerificationView view) => new()
    {
        ["state"] = view.State, ["stewardId"] = view.StewardId, ["stewardName"] = view.StewardName, ["verifiedAt"] = view.VerifiedAt,
        ["verifiedUntil"] = view.VerifiedUntil, ["verifiedVersion"] = view.VerifiedVersion, ["changedSinceVerification"] = view.ChangedSinceVerification
    };

    private static string Steward(PageVerificationView view) => view.StewardName is { } name ? ", garant " + McpText.Line(name, 100) : ", bez garanta";

    private static string Changed(PageVerificationView view)
        => view.ChangedSinceVerification ? $", po ověření změněno (ověřena verze {view.VerifiedVersion}, publikována {view.PublishedVersion})" : "";

    private static string Date(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "?";
}
