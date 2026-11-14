namespace Atlas.Server.Features.PersonalApi.Contracts;

/// <summary>GET /api/admin/api-tokens/summary (WikiAdmin).</summary>
/// <param name="LevelsReview">The upgrade to token rights converted accounts and tokens and no WikiAdmin has confirmed the review yet:
/// the security overview shows the item api_levels_review.</param>
public sealed record ApiTokensSummary(bool LevelsReview);
