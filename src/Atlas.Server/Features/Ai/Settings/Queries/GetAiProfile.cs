namespace Atlas.Server.Features.Ai.Settings.Queries;

/// <param name="Kind"><c>chat</c>, <c>embeddings</c> or <c>quick</c>; anything else answers 404.</param>
public sealed record GetAiProfile(string Kind);
