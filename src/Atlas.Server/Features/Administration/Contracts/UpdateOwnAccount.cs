namespace Atlas.Server.Features.Administration.Contracts;

public sealed record UpdateOwnAccount(string DisplayName, string? Email, string ExpectedVersion);
