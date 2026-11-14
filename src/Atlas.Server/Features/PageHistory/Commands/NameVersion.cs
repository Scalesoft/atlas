namespace Atlas.Server.Features.PageHistory.Commands;

/// <summary>„Uložit verzi“ (3.6-C rule 1): the working revision becomes a named point of the history and ends its version; the next
/// change opens a new one. <paramref name="Label"/> is optional.</summary>
public sealed record NameVersion(Guid Id, int ExpectedVersion, string? Label);

public sealed record NamedVersion(Guid RevisionId, int Version);
