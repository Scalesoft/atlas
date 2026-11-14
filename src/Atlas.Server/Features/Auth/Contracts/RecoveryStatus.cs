namespace Atlas.Server.Features.Auth.Contracts;

public sealed record RecoveryStatus(bool Available, bool BuiltinEnabled, string TokenFilePath, string Instructions);
