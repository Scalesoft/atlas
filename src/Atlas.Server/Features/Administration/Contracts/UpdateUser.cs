namespace Atlas.Server.Features.Administration.Contracts;

public sealed record UpdateUser(string DisplayName, bool IsActive, bool IsWikiAdmin, string ExpectedVersion);
