namespace Atlas.Server.Features.Ai.Settings.Queries;

/// <summary>The configuration cache's load (<see cref="AiConfigurationCache"/>): no actor, no access check; the snapshot never
/// leaves the server (keys stay data-protected in it).</summary>
public sealed record LoadAiConfiguration;
