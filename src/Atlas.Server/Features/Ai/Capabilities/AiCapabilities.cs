using Atlas.Server.Features.Ai.Availability;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Domain;

namespace Atlas.Server.Features.Ai.Capabilities;

/// <summary>
/// <c>GET /api/ai/capabilities</c>: what the signed-in user's UI may show (AI-24, AI-25). Booleans only, never a provider, model,
/// state or reason; the client takes a missing field as false, so later phases only add fields. Phase 3 adds <see cref="Answer"/>
/// (Odpověď z wiki in the omnisearch, <c>search.answer</c>) and <see cref="Assistant"/> (the assistant panel, <c>chat.answer</c>).
/// </summary>
public sealed record AiCapabilitiesDto(bool SemanticSearch, bool Related, bool EditorLinks, bool CreateSuggestions, bool Answer, bool Assistant, bool EditorIntent = false, bool EditorTransform = false, bool EditorOutline = false, bool PublishCheck = false, bool ChangeSummary = false, bool PageSummary = false, bool TagSuggestions = false, bool Agent = false, bool ArrangePages = false);

/// <summary>
/// Whether a task may run now, from memory only (<see cref="AiConfigurationCache"/> and <see cref="AiAvailability"/>): the master
/// switch is on, the task is switched on, its profile (<see cref="AiConfigurationSnapshot.ProfileFor"/>) is configured, enabled and
/// <see cref="AiProfileState.Available"/>; an embeddings task needs an active index generation built with the current revision's
/// fingerprint, a task over the stored chunks (<see cref="AiTaskDefinition.RequiresIndex"/>: the answers of phase 3) just an
/// active generation, whatever the state of the embeddings (R3, O1); a task over working text on a cloud profile also needs the
/// admin's permission (<see cref="MaySendInput"/>). <c>page.related</c> calls no model when read, yet follows the embeddings
/// profile too (O4: one dependency per function).
/// </summary>
public sealed class AiCapabilities(AiConfigurationCache cache, AiAvailability availability)
{
    public async ValueTask<AiCapabilitiesDto> GetAsync(CancellationToken ct)
    {
        var configuration = await cache.GetAsync(ct);
        return new AiCapabilitiesDto(Allows(configuration, AiTaskCatalog.SearchEmbedQuery), Allows(configuration, AiTaskCatalog.PageRelated),
            Allows(configuration, AiTaskCatalog.EditorLink), Allows(configuration, AiTaskCatalog.PageCreateSuggest),
            Allows(configuration, AiTaskCatalog.SearchAnswer), Allows(configuration, AiTaskCatalog.ChatAnswer),
            Allows(configuration, AiTaskCatalog.EditorIntent), Allows(configuration, AiTaskCatalog.EditorTransform),
            Allows(configuration, AiTaskCatalog.EditorOutline), Allows(configuration, AiTaskCatalog.PublishCheck),
            Allows(configuration, AiTaskCatalog.ChangeSummary), Allows(configuration, AiTaskCatalog.PageSummary), Allows(configuration, AiTaskCatalog.TagSuggest),
            Allows(configuration, AiTaskCatalog.AssistantAgent), Allows(configuration, AiTaskCatalog.AssistantAgent) && Allows(configuration, AiTaskCatalog.AssistantArrange));
    }

    public async ValueTask<bool> IsAvailableAsync(string task, CancellationToken ct) => Allows(await cache.GetAsync(ct), task);

    /// <summary>The same decision against a snapshot the caller already holds (one snapshot for a whole request).</summary>
    public bool Allows(AiConfigurationSnapshot configuration, string task)
    {
        if (AiTaskCatalog.Find(task) is not { } definition || !configuration.Enabled || !configuration.TaskEnabled(task))
        {
            return false;
        }
        if (configuration.ProfileFor(task) is not { Enabled: true } profile || !availability.IsAvailable(profile))
        {
            return false;
        }
        if (!MaySendInput(configuration, definition, profile))
        {
            return false;
        }
        if (definition.RequiresIndex && configuration.ActiveGeneration is null)
        {
            return false;
        }
        return definition.ProfileKind != AiProfileKind.Embeddings
            || configuration.ActiveGeneration is { } generation && string.Equals(generation.Fingerprint, profile.Fingerprint, StringComparison.Ordinal);
    }

    /// <summary>Z2/D3: an author's working text (E2, E4, P1–P3) reaches a cloud profile only when the admin allowed it for the
    /// task (<c>cloudWorkingText</c>); a company profile always may. Without the permission the task is unavailable, so its
    /// capability is false and its pure-AI endpoints answer 503, while their rule-based parts keep working. The callers that
    /// send working text check it again against the profile they call (<c>WorkingTextEmbedder</c>). The answers of phase 3 send a
    /// question and published content only (<see cref="AiInputClass.Published"/>), which go wherever the admin pointed the chat
    /// profile.</summary>
    public static bool MaySendInput(AiConfigurationSnapshot configuration, AiTaskDefinition task, AiProfileSnapshot profile)
        => task.Input != AiInputClass.WorkingText || !profile.IsCloud
            || configuration.Tasks.TryGetValue(task.Code, out var setting) && setting.CloudWorkingText;
}
