namespace Atlas.Server.Features.PageHistory.Contracts;

public sealed record RestorePageRequest(Guid RevisionId, int ExpectedVersion);
