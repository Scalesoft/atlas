namespace Atlas.Server.Features.Administration.Contracts;

public sealed record CreateBuiltinUser(string Username, string DisplayName, bool IsWikiAdmin = false);
