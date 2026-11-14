namespace Atlas.Server.Features.KnowledgeMaintenance;

public sealed record GetAiUsageReport;
public sealed record GetAiQualityReport;
public sealed record AiUsageReport(DateTime Since, DateTime Until, IReadOnlyList<AiUsageRow> Items)
{
    public IReadOnlyList<AiUsageProviderRow> Providers { get; init; } = [];
    public IReadOnlyList<AiUsageDayRow> Days { get; init; } = [];
}

public sealed record AiUsageProviderRow(string Provider, string Location, string Model, long Calls, long Errors, long Canceled,
    long? InputTokens, long? OutputTokens, long InputKnownCalls, long OutputKnownCalls, long CostKnownCalls,
    decimal? EstimatedCost, DateTime LastCallAt);
public sealed record AiUsageDayRow(DateTime Day, long Calls, long Errors, long Canceled, long? InputTokens,
    long? OutputTokens, long InputKnownCalls, long OutputKnownCalls, long CostKnownCalls, decimal? EstimatedCost);
public sealed record AiUsageRow(string Task, string Kind, string Operation, long Calls, long Errors, long? InputTokens,
    long? OutputTokens, long InputKnownCalls, long OutputKnownCalls, double P50Ms, double P95Ms, decimal? EstimatedCost)
{
    public long CostKnownCalls { get; init; }
}
public sealed record AiQualityReport(DateTime Since, DateTime Until, IReadOnlyList<AiQualityRow> Items, IReadOnlyList<AiInteractionRow> Interactions, IReadOnlyList<AiNoSourcesRow> NoSources);
public sealed record AiQualityRow(string Task, string? PromptVersion, string Rating, string? Reason, long Count);

public sealed record AiNoSourcesRow(Guid? SpaceId, string SpaceName, long Count);

public sealed record AiInteractionRow(string Task, string Kind, string Outcome, long Count);
