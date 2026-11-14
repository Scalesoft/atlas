namespace Atlas.Server.Features.Auth.Contracts;

/// <summary>
/// The caller's own AI preferences (G4, Q6). Version is null until the first save; the defaults are then suggestions on, no type disabled,
/// automatic answers in search ('auto'; 'ask' answers on demand, 'never' hides it) and the current space as assistant scope ('context' or 'wiki').
/// </summary>
public sealed record AiPreferencesView(string EditorSuggestions, string[] DisabledSuggestionTypes, string? Version, string SearchAnswer = "auto", string AssistantScope = "context");

/// <summary>SearchAnswer and AssistantScope are optional: a client that does not send them keeps the stored values (or the defaults).</summary>
public sealed record SaveAiPreferencesRequest(string EditorSuggestions, string[] DisabledSuggestionTypes, string? ExpectedVersion, string? SearchAnswer = null, string? AssistantScope = null);
