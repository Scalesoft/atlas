namespace Atlas.Server.Features.PageLinks.Queries;

public sealed record CheckPublication(Guid PageId, int ExpectedVersion);
