using System.Text;
using Microsoft.Extensions.AI;
using Atlas.Server.Features.Ai.Settings.Domain;

namespace Atlas.Server.Features.Ai.Providers;

/// <summary>Which prefix of the embeddings profile a text gets. Callers prepare the text themselves
/// (<see cref="AiProfileSnapshot.Prepare"/>), not the pipeline, so a test sees exactly what is embedded.</summary>
public enum AiEmbeddingPurpose
{
    Query,
    Document,
}

/// <summary>
/// An immutable copy of one saved <see cref="AiProfileRevision"/>: what <see cref="IAiClientFactory"/> builds a client from.
/// The key stays data-protected here (<see cref="ProtectedApiKey"/>); the factory unprotects it only while building the
/// provider client, so no plain key lives outside that client. <see cref="ToString"/> never prints the key.
/// </summary>
public sealed record AiProfileSnapshot(
    Guid RevisionId,
    string Kind,
    bool Enabled,
    string Provider,
    string Location,
    string? BaseUrl,
    string Model,
    string? ModelVersion,
    int TimeoutSeconds,
    int MaxConcurrency,
    string? ReasoningEffort,
    float? Temperature,
    bool NativeJsonSchema,
    int? Dimensions,
    string QueryPrefix,
    string DocumentPrefix,
    bool RequestDimensions,
    bool UseChatProfile,
    decimal? InputPricePerMillion,
    decimal? OutputPricePerMillion,
    string? Fingerprint)
{
    /// <summary>The official API when an Anthropic profile leaves the URL empty.</summary>
    public const string AnthropicDefaultBaseUrl = "https://api.anthropic.com";

    /// <summary>Data-protected with <c>SaveAiProfileHandler.ApiKeyPurpose</c>; "" = no key.</summary>
    public string ProtectedApiKey { get; init; } = "";

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    public bool IsCloud => Location == AiLocation.Cloud;

    /// <summary>The host calls go to, for logs: never a path, query or header.</summary>
    public string Host => Uri.TryCreate(BaseUrl ?? (Provider == AiProvider.Anthropic ? AnthropicDefaultBaseUrl : ""), UriKind.Absolute, out var uri)
        ? uri.Host
        : "(none)";

    /// <summary>The profile's reasoning effort; unset means <see cref="Microsoft.Extensions.AI.ReasoningEffort.None"/>, the default
    /// of every 2.0 task (design §15: Gemma 4 without it spends ~900 tokens thinking per paragraph).</summary>
    public Microsoft.Extensions.AI.ReasoningEffort Effort => ReasoningEffort switch
    {
        "low" => Microsoft.Extensions.AI.ReasoningEffort.Low,
        "medium" => Microsoft.Extensions.AI.ReasoningEffort.Medium,
        "high" => Microsoft.Extensions.AI.ReasoningEffort.High,
        _ => Microsoft.Extensions.AI.ReasoningEffort.None,
    };

    /// <summary>The text as the embeddings profile wants it: its query or document prefix (verbatim, part of the fingerprint)
    /// before the text.</summary>
    public string Prepare(string text, AiEmbeddingPurpose purpose)
        => (purpose == AiEmbeddingPurpose.Query ? QueryPrefix : DocumentPrefix) + text;

    public static AiProfileSnapshot From(AiProfileRevision revision) => new(
        revision.Id, revision.Kind, revision.Enabled, revision.Provider, revision.Location, revision.BaseUrl, revision.Model,
        revision.ModelVersion, revision.TimeoutSeconds, revision.MaxConcurrency, revision.ReasoningEffort, revision.Temperature,
        revision.NativeJsonSchema, revision.Dimensions, revision.QueryPrefix, revision.DocumentPrefix, revision.RequestDimensions,
        revision.UseChatProfile, revision.InputPricePerMillion, revision.OutputPricePerMillion, revision.Fingerprint)
    {
        ProtectedApiKey = revision.ProtectedApiKey,
    };

    /// <summary>Identifies the profile in diagnostics without any secret, URL or prefix.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Kind = ").Append(Kind).Append(", RevisionId = ").Append(RevisionId).Append(", Provider = ").Append(Provider)
            .Append(", Model = ").Append(Model);
        return true;
    }
}
