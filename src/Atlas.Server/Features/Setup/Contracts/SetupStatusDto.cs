using Atlas.Server.Features.Auth.Contracts;

namespace Atlas.Server.Features.Setup.Contracts;

public sealed record SetupStatusDto(bool DatabaseReady, bool RequiresSetup, bool BootstrapTokenRequired, int PasswordMinimumLength, string? WorkspaceName, SetupTokenSource? TokenSource, string Version);
