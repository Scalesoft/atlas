namespace Atlas.Server.Features.Ai.Settings.Contracts;

/// <summary>The switchable tasks of <c>AiTaskCatalog</c> in catalog order.</summary>
public sealed record AiFeaturesView(IReadOnlyList<AiFeatureItem> Items, string? Version);

/// <param name="CloudWorkingText">Whether the task may send an author's working text to a cloud profile; null when the
/// task does not send working text.</param>
/// <param name="Limitable">Whether the admin may set <see cref="DailyLimit"/> (the answer tasks of phase 3).</param>
/// <param name="DailyLimit">Answers per user and server calendar day; null = no limit (the default, O10) and always null for
/// a task that is not limitable.</param>
public sealed record AiFeatureItem(string Task, string Group, bool Enabled, string ProfileKind, bool? CloudWorkingText,
    bool Limitable = false, int? DailyLimit = null);

/// <summary>Tasks the request does not list keep their current value.</summary>
public sealed record SaveAiFeaturesRequest(IReadOnlyList<SaveAiFeatureItem> Items, string? ExpectedVersion);

/// <param name="CloudWorkingText">Only for working-text tasks; null keeps the current value there and must be null
/// everywhere else.</param>
/// <param name="DailyLimit">Only for limitable tasks, 1–10 000; null = no limit. Unlike <paramref name="CloudWorkingText"/> it is
/// not kept when left out: a client listing a limitable task sends its limit (or null) every time.</param>
public sealed record SaveAiFeatureItem(string Task, bool Enabled, bool? CloudWorkingText, int? DailyLimit = null);
