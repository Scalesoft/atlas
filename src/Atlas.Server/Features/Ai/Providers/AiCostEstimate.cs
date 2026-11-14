namespace Atlas.Server.Features.Ai.Providers;

/// <summary>The cost the overview shows, from the profile revision's prices per million tokens. An estimate only: providers
/// bill by their own counts, cached input and reasoning tokens differ, and unknown token counts make it unknown.</summary>
public static class AiCostEstimate
{
    public static decimal? Estimate(long? inputTokens, long? outputTokens, decimal? inputPricePerMillion, decimal? outputPricePerMillion)
    {
        if (inputTokens is null && outputTokens is null)
        {
            return null;
        }
        if ((inputTokens is not null && inputPricePerMillion is null) || (outputTokens is not null && outputPricePerMillion is null))
        {
            return null;
        }
        return ((inputTokens ?? 0) * (inputPricePerMillion ?? 0) + (outputTokens ?? 0) * (outputPricePerMillion ?? 0)) / 1_000_000m;
    }
}
