namespace Atlas.Server.Features.Auth.Contracts;

public sealed record SetupTokenSource(string Kind, string FilePath, string Instructions);
