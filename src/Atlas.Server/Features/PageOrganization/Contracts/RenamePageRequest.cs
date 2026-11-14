namespace Atlas.Server.Features.PageOrganization.Contracts;

public sealed record RenamePageRequest(string Title, int ExpectedVersion);
