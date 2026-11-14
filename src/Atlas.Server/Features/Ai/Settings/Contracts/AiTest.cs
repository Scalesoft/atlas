namespace Atlas.Server.Features.Ai.Settings.Contracts;

/// <summary>The answer of "Test spojení": an outcome code, never text from the provider.</summary>
/// <param name="Code">An <c>AiOutcome</c> code when <paramref name="Ok"/> is false.</param>
/// <param name="Dimensions">Embeddings: the length of the returned vector.</param>
/// <param name="Model">The model that answered, as the provider names it.</param>
/// <param name="Warning">A working connection that still needs attention (<see cref="AiProfileStatus.ModelMismatch"/>).</param>
public sealed record AiTestResult(bool Ok, string? Code, int LatencyMs, int? Dimensions, string? Model, string? Warning = null);
