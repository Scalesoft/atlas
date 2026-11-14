namespace Atlas.Server.Features.ModuleSettings.Contracts;

public sealed record OidcConnectionTest(bool Ok, DateTime CheckedAt, string Message);
