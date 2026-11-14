namespace Atlas.Server.Features.PageHistory.Contracts;

/// <summary>„Uložit verzi“: the working revision the client saw and an optional one-line description.</summary>
public sealed record NameVersionRequest(int ExpectedVersion, string? Label = null);
