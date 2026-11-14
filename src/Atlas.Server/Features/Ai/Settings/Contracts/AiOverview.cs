namespace Atlas.Server.Features.Ai.Settings.Contracts;

/// <summary>Administrace → AI → Přehled (<c>GET /api/admin/ai</c>, WikiAdmin): the master switch, each profile's state and a
/// week of use. Codes and times only; no upstream text, URL or key.</summary>
/// <param name="SwitchVersion">The master switch's CAS version (<see cref="AiSwitchView.Version"/>), null until saved.</param>
/// <param name="Profiles">Every profile kind (chat, embeddings, quick), configured or not.</param>
/// <param name="Index">The index tile; null until the index exists (V2 fills it through <see cref="IAiIndexOverview"/>).</param>
public sealed record AiOverview(bool Enabled, string? SwitchVersion, IReadOnlyList<AiProfileStatus> Profiles, AiIndexSummary? Index,
    AiUsageSummary Usage7Days);

/// <param name="State"><c>NotConfigured</c> | <c>Disabled</c> | <c>Unknown</c> | <c>Available</c> | <c>Unavailable</c>
/// (<see cref="Availability.AiProfileState"/>).</param>
/// <param name="Since">When the profile became <c>Available</c> or <c>Unavailable</c>.</param>
/// <param name="LastErrorCode">The last failure's <c>AiOutcome</c> code since the revision was saved; never upstream text.</param>
/// <param name="Warning">What needs the admin's attention although the profile works (the backups' <c>warning</c>); today the
/// Gateway server serving another model than the profile names.</param>
public sealed record AiProfileStatus(string Kind, string State, DateTime? Since, string? LastErrorCode, DateTime? LastSuccessAt,
    string? Model, string? Location, bool UseChatProfile = false, string? Warning = null)
{
    public static string ModelMismatch(string configured, string served)
        => $"Server Gateway obsluhuje model „{served}“, ale profil má nastavený „{configured}“. AI funguje dál; upravte model v profilu, aby odpovídal.";
}

/// <param name="Errors">Calls that failed (any outcome but <c>ok</c> and <c>canceled</c>).</param>
/// <param name="InputTokens">Null when no call reported tokens (LM Studio embeddings report none).</param>
/// <param name="EstimatedCost">From the revisions' prices per million tokens; an estimate, null without prices or tokens.</param>
public sealed record AiUsageSummary(int Calls, int Errors, long? InputTokens, long? OutputTokens, decimal? EstimatedCost);

/// <summary>The index tile (V2 fills it; the shape is agreed with the web client, <c>aiAdmin.ts</c>).</summary>
/// <param name="State"><c>None</c> | <c>Building</c> | <c>Active</c>.</param>
/// <param name="Model">The active (or building) generation's model.</param>
/// <param name="RebuildRecommended">The active generation was chunked by an older chunker.</param>
public sealed record AiIndexSummary(string State, string? Model, int EligiblePages, int IndexedPages, AiIndexQueueSummary Queue,
    bool RebuildRecommended);

public sealed record AiIndexQueueSummary(int Pending, int Failing, DateTime? OldestPendingAt);
