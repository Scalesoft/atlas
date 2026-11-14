namespace Atlas.Server.Features.Ai.Settings.Contracts;

/// <summary>
/// One profile kind (<c>chat</c>, <c>embeddings</c>, <c>quick</c>). Never carries the key, only <see cref="HasApiKey"/>.
/// <see cref="Configured"/> is false (and Version null) until the first save. <see cref="ModelLockedByIndex"/>: an index
/// generation is active, so a change of model, version, dimensions or prefixes needs the model exchange.
/// </summary>
public sealed record AiProfileView(string Kind, bool Configured, bool Enabled, string? Provider, string? Location,
    string? BaseUrl, string? Model, string? ModelVersion, int? Dimensions, string QueryPrefix, string DocumentPrefix,
    int TimeoutSeconds, int MaxConcurrency, string? ReasoningEffort, float? Temperature, bool NativeJsonSchema,
    bool RequestDimensions, bool UseChatProfile, decimal? InputPricePerMillion, decimal? OutputPricePerMillion,
    bool HasApiKey, bool ModelLockedByIndex, string? Version);

/// <summary>
/// A whole profile section. <see cref="ApiKey"/> null or blank keeps the stored key (unless the origin changes),
/// <see cref="ClearApiKey"/> removes it. Fields that do not apply to the kind are ignored: dimensions, prefixes and
/// RequestDimensions outside embeddings, reasoning effort, temperature and the output price for embeddings,
/// UseChatProfile outside quick tasks.
/// </summary>
public sealed record SaveAiProfileRequest(bool Enabled, string Provider, string Location, string? BaseUrl, string Model,
    string? ModelVersion, int? Dimensions, string? QueryPrefix, string? DocumentPrefix, int TimeoutSeconds, int MaxConcurrency,
    string? ReasoningEffort, float? Temperature, bool NativeJsonSchema, bool RequestDimensions, bool UseChatProfile,
    decimal? InputPricePerMillion, decimal? OutputPricePerMillion, string? ApiKey, bool ClearApiKey, string? ExpectedVersion)
{
    public override string ToString() => "SaveAiProfileRequest { ApiKey = [redacted] }";
}
